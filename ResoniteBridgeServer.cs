using System;
using System.Threading;
using System.Threading.Tasks;
using MemoryMappedFileIPC;
using System.Collections.Concurrent;

namespace ResoniteBridgeLib
{
    public class ResoniteBridgeServer : IDisposable
    {
        public const int millisBetweenPing = ResoniteBridgeClient.millisBetweenPing;

        public IpcPublisher publisher;
        public IpcSubscriber subscriber;

        CancellationTokenSource stopToken;

        private readonly ConcurrentDictionary<string, MessageProcessor> processors = new ConcurrentDictionary<string, MessageProcessor>();

        public bool TryGetProcessor(string methodName, out MessageProcessor processor)
        {
            return processors.TryGetValue(methodName, out processor);
        }

        public void RegisterProcessor(string methodName, MessageProcessor processor)
        {
            processors[methodName] = processor;
        }

        public bool TryUnregisterProcessor(string methodName, out MessageProcessor removedProcessor)
        {
            return processors.TryRemove(methodName, out removedProcessor);
        }

        private ConcurrentQueueWithNotification<ResoniteBridgeMessage> outputMessages = new ConcurrentQueueWithNotification<ResoniteBridgeMessage>();

        private Thread sendingThread;
        
        public delegate Task<byte[]> MessageProcessor(byte[] input);

        private async Task ProcessMessage(ResoniteBridgeMessage message, int timeout = -1)
        {
            try
            {
                if (!TryGetProcessor(message.methodName, out MessageProcessor processor))
                {
                    DebugLog("Unknown processor " + message.methodName);
                    throw new UnknownProcessorException(message.methodName);
                }

                DebugLog("Running processor " + message.methodName);
                using (CancellationTokenSource tmpStopToken = new CancellationTokenSource())
                {
                    using (CancellationTokenSource linkedToken = CancellationTokenSource.CreateLinkedTokenSource(stopToken.Token, tmpStopToken.Token))
                    {
                        Task<byte[]> processingTask = Task.Run(async () => await processor(message.data), linkedToken.Token);

                        // Create a task for disconnect event
                        // important to call this wrapper because just () => WaitOne will consume the thread forever even if cancel token is canceled
                        Task disconnectTask = IpcUtils.WaitOneAsync(publisher.disconnectEvent, linkedToken.Token);

                        Task timeoutTask = Task.Delay(timeout, linkedToken.Token);

                        // Wait for the first task to complete
                        Task completedTask = await Task.WhenAny(
                            processingTask,
                            disconnectTask,
                            timeoutTask
                        );

                        byte[] responseData = null;
                        if (completedTask == processingTask)
                        {
                            responseData = await processingTask;
                        }

                        // clean up the other tasks
                        tmpStopToken.Cancel();
                        try
                        {
                            await processingTask;
                        }
                        catch (TaskCanceledException ex)
                        {
                        }
                        try
                        {
                            await disconnectTask;
                        }
                        catch (TaskCanceledException ex)
                        {
                        }

                        try
                        {
                            await timeoutTask;
                        }
                        catch (TaskCanceledException ex)
                        {
                        }

                        DebugLog("Done running processor " + message.methodName);
                        if (stopToken.IsCancellationRequested)
                        {
                            DebugLog("Done running processor " + message.methodName + " canceled");
                            throw new CanceledException();
                        }
                        else if (completedTask == disconnectTask)
                        {
                            DebugLog("Done running processor " + message.methodName + " disconnected");
                            throw new DisconnectException();
                        }
                        else if (completedTask == timeoutTask)
                        {
                            DebugLog("Done running processor " + message.methodName + " timed out");
                            throw new TimeoutException();
                        }
                        else
                        {
                            DebugLog("Done running processor " + message.methodName + " success");
                            ResoniteBridgeMessage response = new ResoniteBridgeMessage()
                            {
                                data = responseData,
                                messageType = ResoniteBridgeValueType.Bytes,
                                methodName = message.methodName,
                                uuid = message.uuid
                            };
                            outputMessages.Enqueue(response);
                        }
                    }

                }

            }
            catch (Exception ex)
            {
                DebugLog("Got exception when running processor " + ex.GetType() + " " + ex.Message + " " + ex.StackTrace);
                ResoniteBridgeMessage response = new ResoniteBridgeMessage()
                {
                    data = SerializationUtils.EncodeString(ex.ToString() + " " + ex.StackTrace),
                    messageType = ResoniteBridgeValueType.Error,
                    methodName = message.methodName,
                    uuid = message.uuid,
                };
                outputMessages.Enqueue(response);
            }
        }

        public int NumActiveConnections()
        {
            return Math.Min(subscriber.NumActiveConnections(), publisher.NumActiveConnections());
        }

        readonly IpcUtils.DebugLogType DebugLog;

        public ResoniteBridgeServer(string channelName, string serverDirectory, IpcUtils.DebugLogType DebugLog)
        {
            this.DebugLog = DebugLog;
            DebugLog("Hii i'm the bridge server");
            stopToken = new CancellationTokenSource();
            publisher = new IpcPublisher(channelName + "client", serverDirectory, millisBetweenPing, msg => DebugLog(msg));
            subscriber = new IpcSubscriber(channelName + "server", serverDirectory, millisBetweenPing, msg => DebugLog(msg));

            subscriber.RecievedBytes += async (byte[][] bytes) =>
            {
                DebugLog("Subscriber recieved these bytes " + bytes);

                DebugLog("Subscriber recieved " + bytes.Length + " bytes");
                ResoniteBridgeMessage parsedMessage = (ResoniteBridgeMessage)SerializationUtils.DecodeObject< ResoniteBridgeMessage>(
                    bytes[0]);
                parsedMessage.data = bytes[1];
                DebugLog("Recieved message " + parsedMessage.methodName + " with " + 
                    (parsedMessage.data == null ? 0 : parsedMessage.data.Length)
                    + " bytes");
                await ProcessMessage(parsedMessage);
                DebugLog("Subscriber done " + bytes);
            };
            
            // network monitoring thread
            sendingThread = new Thread(() =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        // Read the request from the client. Once the client has
                        // written to the pipe its security token will be available.

                        outputMessages.TryDequeue(out ResoniteBridgeMessage response, -1, stopToken.Token);
                        // wait for publisher to connect
                        WaitHandle.WaitAny(new WaitHandle[] { stopToken.Token.WaitHandle, publisher.connectEvent });
                        if (stopToken.IsCancellationRequested)
                        {
                            break;
                        }
                        byte[] messageData = response.data;
                        response.data = new byte[0];
                        byte[] encodedBytes = SerializationUtils.EncodeObject(response);
                        publisher.Publish(new byte[][] { encodedBytes, messageData });
                    }
                }
                catch (TaskCanceledException)
                {

                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    DebugLog("Got exception in server sending thread for channel " + channelName + " with exception " + ex.GetType() + " " + ex.Message + " " + ex.StackTrace);
                }
            });
            
            sendingThread.Start();
        }

        public void Dispose()
        {
            if (stopToken != null && !stopToken.IsCancellationRequested)
            {
                stopToken.Cancel();
            }
            if (sendingThread != null)
            {
                sendingThread.Join();
                sendingThread = null;
            }
            if (publisher != null)
            {
                publisher.Dispose();
                publisher = null;
            }
            if (subscriber != null)
            {
                subscriber.Dispose();
                subscriber = null;
            }
            if (stopToken != null)
            {
                stopToken.Dispose();
                stopToken = null;
            }
            if (outputMessages != null)
            {
                outputMessages.Dispose();
                outputMessages = null;
            }
        }
    }
}
