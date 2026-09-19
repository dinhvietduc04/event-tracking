using System.Text;
using System.Text.Json;
using EventTracking.Persistence;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace EventTracking.Worker;

/// <summary>Owns durable topology, publisher confirmations and consumer acknowledgements.</summary>
public sealed class RabbitMqWorker(PostgresStore store, ClickHouseProjector clickHouse, ClickHouseOptions clickHouseOptions, RabbitMqOptions options, StorageOptions storage,
    ILogger<RabbitMqWorker> logger) : BackgroundService
{
    private readonly ConnectionFactory factory = new() { Uri = new Uri(options.Uri), DispatchConsumersAsync = true, AutomaticRecoveryEnabled = true };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning(error, "RabbitMQ worker connection failed; durable outbox work remains retryable.");
                await Task.Delay(storage.PollIntervalMilliseconds, stoppingToken);
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        if (clickHouseOptions.Enabled) await clickHouse.EnsureSchemaAsync(ct);
        using var connection = factory.CreateConnection("event-tracking-worker");
        using var publisher = connection.CreateModel();
        using var consumerChannel = connection.CreateModel();
        Declare(publisher); Declare(consumerChannel);
        publisher.ConfirmSelect();
        publisher.BasicReturn += (_, ea) =>
        {
            logger.LogWarning("Message {MessageId} was unroutable (reply code: {ReplyCode}, reply text: {ReplyText})",
                ea.BasicProperties?.MessageId, ea.ReplyCode, ea.ReplyText);
        };
        publisher.CallbackException += (_, ea) =>
        {
            logger.LogError(ea.Exception, "RabbitMQ publisher channel callback exception: {Detail}", ea.Detail);
        };
        consumerChannel.CallbackException += (_, ea) =>
        {
            logger.LogError(ea.Exception, "RabbitMQ consumer channel callback exception: {Detail}", ea.Detail);
        };
        consumerChannel.BasicQos(0, (ushort)options.Prefetch, false);
        long consumedCounter = 0;
        var consumer = new AsyncEventingBasicConsumer(consumerChannel);
        consumer.Received += async (_, delivery) =>
        {
            BrokerMessage? message = null;
            try
            {
                message = JsonSerializer.Deserialize<BrokerMessage>(delivery.Body.ToArray());
                if (message is null) throw new InvalidOperationException("Broker body is empty.");
                await store.CompleteBrokerDeliveryAsync(message, ct);
                consumerChannel.BasicAck(delivery.DeliveryTag, false); // Only after the PostgreSQL commit.
                Interlocked.Increment(ref consumedCounter);
                logger.LogInformation("Completed broker delivery for project {ProjectId}, event {EventId}", message.ProjectId, message.EventId);
            }
            catch (Exception error) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(error, "Broker delivery could not be projected.");
                if (message is null) consumerChannel.BasicReject(delivery.DeliveryTag, false);
                else
                {
                    bool permanent = IsPermanentFailure(error);
                    await store.RecordBrokerFailureAsync(message, error, permanent, ct);
                    consumerChannel.BasicAck(delivery.DeliveryTag, false);
                }
            }
        };
        string tag = consumerChannel.BasicConsume(options.Queue, autoAck: false, consumer);
        long totalPublished = 0, totalConsumed = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                int published = await store.PublishBrokerOutboxAsync(message => PublishAsync(publisher, message, ct), ct);
                int mirrored = await clickHouse.ProcessBatchAsync(ct);
                totalPublished += published;
                var status = await store.OutboxStatusAsync(null, ct);
                if (published > 0)
                {
                    logger.LogInformation(
                        "Published {Count} outbox messages to RabbitMQ exchange {Exchange} in {ElapsedMs}ms; totals published={TotalPublished} consumed={TotalConsumed} pending={Pending} retrying={Retrying} deadLettered={DeadLettered} oldestPendingSec={OldestSec}",
                        published, options.Exchange, stopwatch.ElapsedMilliseconds, totalPublished, Volatile.Read(ref consumedCounter),
                        status.Pending, status.Retrying, status.DeadLettered, status.OldestPendingSeconds);
                    totalConsumed = Volatile.Read(ref consumedCounter);
                }
                else if (mirrored > 0)
                {
                    logger.LogInformation("Mirrored {Count} events to ClickHouse in {ElapsedMs}ms", mirrored, stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    await Task.Delay(storage.PollIntervalMilliseconds, ct);
                }
            }
        }
        finally
        {
            if (consumerChannel.IsOpen) consumerChannel.BasicCancel(tag);
        }
    }

    private static bool IsPermanentFailure(Exception error) =>
        error is JsonException or InvalidOperationException or ArgumentException or FormatException;

    private Task PublishAsync(IModel channel, BrokerMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var properties = channel.CreateBasicProperties();
        properties.Persistent = true;
        properties.ContentType = "application/json";
        properties.MessageId = message.EventId.ToString("D");
        properties.Type = "event-tracking.v1";
        channel.BasicPublish(options.Exchange, "events", mandatory: true, properties,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)));
        if (!channel.WaitForConfirms(TimeSpan.FromSeconds(10))) throw new IOException("RabbitMQ did not confirm publication.");
        return Task.CompletedTask;
    }

    private void Declare(IModel channel)
    {
        channel.ExchangeDeclare(options.Exchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.ExchangeDeclare(options.DeadLetterExchange, ExchangeType.Direct, durable: true, autoDelete: false);
        channel.QueueDeclare(options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false);
        channel.QueueBind(options.DeadLetterQueue, options.DeadLetterExchange, "dead-letters");

        var queueArgs = new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = options.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = "dead-letters"
        };
        channel.QueueDeclare(options.Queue, durable: true, exclusive: false, autoDelete: false, arguments: queueArgs);
        channel.QueueBind(options.Queue, options.Exchange, "events");
    }
}
