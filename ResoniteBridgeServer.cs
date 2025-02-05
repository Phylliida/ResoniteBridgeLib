﻿using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;
using MemoryMappedFileIPC;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace ResoniteBridgeLib
{
    public class ResoniteBridgeServer : IDisposable
    {
        public const int millisBetweenPing = ResoniteBridgeClient.millisBetweenPing;

        public IpcPublisher publisher;
        public IpcSubscriber subscriber;

        CancellationTokenSource stopToken;

        public delegate void LogDelegate(string message);

        private ConcurrentDictionary<string, MessageProcessor> processors = new ConcurrentDictionary<string, MessageProcessor>();

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
        
        public delegate byte[] MessageProcessor(byte[] input);

        private void ProcessMessageAsync(ResoniteBridgeMessage message, int timeout = -1)
        {
            new Thread(() =>
            {
                ProcessMessageSync(message, timeout);
            }).Start();
        }

        private void ProcessMessageSync(ResoniteBridgeMessage message, int timeout = -1)
        {
            try
            {
                MessageProcessor processor;
                if (!TryGetProcessor(message.methodName, out processor))
                {
                    DebugLog("Unknown processor " + message.methodName);
                    throw new UnknownProcessorException(message.methodName);
                }

                DebugLog("Running processor " + message.methodName);
                Task<byte[]> processingTask = Task.Run(() => processor(message.data), stopToken.Token);

                // Create a task for disconnect event
                Task disconnectTask = Task.Run(() => publisher.disconnectEvent.WaitOne(), stopToken.Token);

                Task timeoutTask = Task.Delay(timeout, stopToken.Token);

                // Wait for the first task to complete
                Task completedTask = Task.WhenAny(
                    processingTask,
                    disconnectTask,
                    timeoutTask
                );
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
                    byte[] responseData = processingTask.GetAwaiter().GetResult();
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
            catch (Exception ex)
            {
                DebugLog("Got exception when running processor " + ex.GetType() + " " + ex.Message + " " + ex.StackTrace);
                ResoniteBridgeMessage response = new ResoniteBridgeMessage()
                {
                    data = ResoniteBridgeUtils.EncodeString(ex.ToString() + " " + ex.StackTrace),
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

        LogDelegate DebugLog;

        public ResoniteBridgeServer(string channelName, string serverDirectory, LogDelegate DebugLog)
        {
            this.DebugLog = DebugLog;
            DebugLog("Hii i'm the bridge server");
            stopToken = new CancellationTokenSource();
            publisher = new IpcPublisher(channelName + "client", serverDirectory, millisBetweenPing, msg => DebugLog(msg));
            subscriber = new IpcSubscriber(channelName + "server", serverDirectory, millisBetweenPing, msg => DebugLog(msg));

            subscriber.RecievedBytes += (byte[][] bytes) =>
            {
                DebugLog("Subscriber recieved these bytes " + bytes);

                try
                {
                    DebugLog("Subscriber recieved " + bytes.Length + " bytes");
                    ResoniteBridgeMessage parsedMessage = (ResoniteBridgeMessage)ResoniteBridgeUtils.DecodeObject< ResoniteBridgeMessage>(
                        bytes[0]);
                    parsedMessage.data = bytes[1];
                    DebugLog("Recieved message " + parsedMessage.methodName + " with " + 
                        (parsedMessage.data == null ? 0 : parsedMessage.data.Length)
                        + " bytes");
                    ProcessMessageAsync(parsedMessage);
                }
                catch (JsonSerializationException e)
                {
                    DebugLog("Failed to deserialize message, ignoring");
                    DebugLog("ERROR: " + e.Message);
                    DebugLog("Message: " + bytes);
                }
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
                        try
                        {
                            byte[] messageData = response.data;
                            response.data = new byte[0];
                            byte[] encodedBytes = ResoniteBridgeUtils.EncodeObject(response);
                            publisher.Publish(new byte[][] { encodedBytes, messageData });
                        }
                        catch (JsonSerializationException e)
                        {
                            DebugLog("Failed to serialize response, ignoring");
                            DebugLog("ERROR: " + e.Message);
                            DebugLog("Message:" + response);
                        }
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
