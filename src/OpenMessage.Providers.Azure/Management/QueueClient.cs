﻿using Microsoft.Extensions.Logging;
using OpenMessage.Providers.Azure.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AzureClient = Microsoft.ServiceBus.Messaging.QueueClient;

namespace OpenMessage.Providers.Azure.Management
{
    internal sealed class QueueClient<T> : ClientBase<T>, IQueueClient<T>
    {
        private readonly AwaitableLazy<AzureClient> _client;
        private readonly IMessageExtension<T>[] _extensions;

        public QueueClient(INamespaceManager<T> namespaceManager,
            ISerializationProvider serializationProvider,
            ILogger<ClientBase<T>> logger,
            IEnumerable<IMessageExtension<T>> extensions)
            : base(serializationProvider, logger)
        {
            if (namespaceManager == null)
                throw new ArgumentNullException(nameof(namespaceManager));

            if (extensions == null)
                throw new ArgumentNullException(nameof(extensions));

            _extensions = extensions.ToArray();
            _client = new AwaitableLazy<AzureClient>(async() =>
            {
                await namespaceManager.ProvisionQueueAsync();
                return namespaceManager.CreateQueueClient();
            });
        }

        public void RegisterCallback(Action<T> callback)
        {
            Task.Run(() => { 
                lock(_client)
                    if (CallbackCount == 0)
                        _client.Value.OnMessage(OnMessage);

                AddCallback(callback);
            });
        }

        public async Task SendAsync(T entity, TimeSpan scheduleIn)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            if (scheduleIn < TimeSpan.Zero)
                throw new ArgumentException("You cannot schedule a message to arrive in the past; time travel isn't a thing yet.");
            
            var message = Serialize(entity);
            if (scheduleIn > TimeSpan.Zero)
                message.ScheduledEnqueueTimeUtc = DateTime.UtcNow.Add(scheduleIn);

            Logger.LogInformation($"Sending message of type: {TypeName}");
            try
            {
                foreach (var extension in _extensions)
                    extension.Extend(message);

                await (await _client).SendAsync(message);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error sending message of type: {TypeName}; Error: {ex.Message}", ex);
                throw;
            }
        }

        public override void Dispose(bool disposing)
        {
            if (_client.IsValueCreated)
                _client.Value?.Close();
        }
    }
}