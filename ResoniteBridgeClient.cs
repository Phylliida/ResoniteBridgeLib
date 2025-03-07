using System;
using System.Collections.Concurrent;
using System.Threading;
using MemoryMappedFileIPC;
using System.Threading.Tasks;

namespace ResoniteBridgeLib
{
    public class ResoniteBridgeClient : IDisposable
    {
        public const string NAMED_SOCKET_KEY = "ResoniteCustomBridge";

        public const int millisBetweenPing = 3000;

        private volatile int curMessageId = 0;

        public IpcPublisher publisher;
        public IpcSubscriber subscriber;

        readonly CancellationTokenSource stopToken;

        public delegate void LogDelegate(string message);

        private readonly ConcurrentQueueWithNotification<ResoniteBridgeMessage> inputMessages = new ConcurrentQueueWithNotification<ResoniteBridgeMessage>();
        private readonly ConcurrentDictionary<int, ManualResetEvent> outputMessageEvents = new ConcurrentDictionary<int, ManualResetEvent>();
        private readonly ConcurrentDictionary<int, ResoniteBridgeMessage> outputMessages = new ConcurrentDictionary<int, ResoniteBridgeMessage>();

        readonly private Thread sendingThread;


        public int NumActiveConnections()
        {
            // sometimes one takes longer than the other to connect, so just take minimum
            return Math.Min(publisher.NumActiveConnections(), subscriber.NumActiveConnections());
        }

        public delegate void MessageResponseHandler(byte[] responseBytes, bool isError);

        public struct MessageResponse
        {
            public byte[] messageBytes;
            public bool isError;
        }
        public async Task<MessageResponse> SendMessage(string name, byte[] data, int timeout)
        {
            int messageUuid = Interlocked.Increment(ref curMessageId);
            DebugLog("Processing message with uuid: " + messageUuid);
            ResoniteBridgeMessage message = new ResoniteBridgeMessage()
            {
                data = data,
                methodName = name,
                messageType = ResoniteBridgeValueType.Bytes,
                uuid = messageUuid,
            };
            try
            {
                ResoniteBridgeMessage responseMessage = await SendMessageHelper(message, timeout);
                return new MessageResponse
                {
                    isError = responseMessage.messageType == ResoniteBridgeValueType.Error,
                    messageBytes = responseMessage.data
                };
            }
            catch (Exception e)
            {
                return new MessageResponse()
                {
                    isError = true,
                    messageBytes = SerializationUtils.EncodeString(e.Message + " " + e.StackTrace)
                };
            }
        }
        private async Task<ResoniteBridgeMessage> SendMessageHelper(ResoniteBridgeMessage message, int timeout=-1)
        {
            ManualResetEvent messageEvent = new ManualResetEvent(false);
            outputMessageEvents[message.uuid] = messageEvent;
            DebugLog("Processing message with uuid: " + message.uuid);
            inputMessages.Enqueue(message);
            int waitedHandle = await IpcUtils.WaitAny(new WaitHandle[]
            {
                stopToken.Token.WaitHandle,
                publisher.disconnectEvent,
                messageEvent
            }, timeout);
            DebugLog("Done processing message with uuid: " + message.uuid);

            outputMessageEvents.TryRemove(message.uuid, out _);
            messageEvent.Dispose();
            
            if (waitedHandle == 0)
            {
                DebugLog("canceled message with uuid: " + message.uuid);
                throw new CanceledException();
            }
            else if (waitedHandle == 1)
            {
                DebugLog("disconnected message with uuid: " + message.uuid);
                throw new DisconnectException();
            }
            else if (waitedHandle == WaitHandle.WaitTimeout)
            {
                DebugLog("timeout message with uuid: " + message.uuid);
                throw new TimeoutException();
            }
            else
            {
                DebugLog("finsihed message with uuid: " + message.uuid);
                if (outputMessages.TryRemove(message.uuid, out ResoniteBridgeMessage response))
                {
                    return response;
                }
                else
                {
                    throw new ArgumentException("Could not find output message, something went wrong");
                }
            }
        }

        public LogDelegate DebugLog;

        public ResoniteBridgeClient(string channelName, string serverDirectory, LogDelegate DebugLog)
        {
            this.DebugLog = DebugLog;
            stopToken = new CancellationTokenSource();
            publisher = new IpcPublisher(channelName + "server", serverDirectory, millisBetweenPing, (msg) => DebugLog(msg));
            subscriber = new IpcSubscriber(channelName + "client", serverDirectory, millisBetweenPing, (msg) => DebugLog(msg));

            // network monitoring thread
            sendingThread = new Thread(() =>
            {
                try
                {
                    while (!stopToken.IsCancellationRequested)
                    {
                        // Read the request from the client. Once the client has
                        // written to the pipe its security token will be available.

                        inputMessages.TryDequeue(out ResoniteBridgeMessage message, -1, stopToken.Token);
                        // wait for publisher to connect
                        WaitHandle.WaitAny(new WaitHandle[] { stopToken.Token.WaitHandle, publisher.connectEvent });
                        if (stopToken.IsCancellationRequested)
                        {
                            break;
                        }
                        // avoid having bytes in memory twice
                        byte[] messageData = message.data;
                        message.data = new byte[0];
                        byte[] encodedBytes = SerializationUtils.EncodeObject(message);
                        publisher.Publish(new byte[][] { encodedBytes, messageData });
                    }
                }
                catch(TaskCanceledException)
                {

                }
                catch (OperationCanceledException)
                {

                }
            });
            
            subscriber.RecievedBytes += (byte[][] bytes) =>
            {
                ResoniteBridgeMessage parsedResult = (ResoniteBridgeMessage)SerializationUtils.DecodeObject< ResoniteBridgeMessage>(
                    bytes[0]);
                parsedResult.data = bytes[1];
                outputMessages[parsedResult.uuid] = parsedResult;
                if (outputMessageEvents.TryGetValue(parsedResult.uuid, out ManualResetEvent evt))
                {
                    try
                    {
                        evt.Set();
                    }
                    catch (ObjectDisposedException)
                    {
                        // this could happen in edge case, ignore it
                    }
                }
                else
                {
                    DebugLog("Event cleaned up, probabaly message failed somehow");
                }
            };
            
            sendingThread.Start();
        }

        public void Dispose()
        {
            if (!stopToken.IsCancellationRequested)
            {
                stopToken.Cancel();
            }
            sendingThread.Join();
            stopToken.Dispose();
            publisher.Dispose();
            subscriber.Dispose();
            inputMessages.Dispose();
        }
    }
}
