using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Serialization.Formatters.Binary;
using System.Threading;
using Microsoft.Ccr.Core;
using MySpace.Common;
using MySpace.ResourcePool;
using MySpace.Shared.Configuration;

namespace MySpace.SocketTransport
{
	public delegate MemoryStream HandleAsyncMessage(int transactionID, MemoryStream messageStream, IPAddress remoteEndPoint);
	/// <summary>
	/// Allows specification whether an endpoint satisfies a whitelist.
	/// </summary>
	/// <param name="remoteEndpoint">The <see cref="IPEndPoint"/> to be checked.</param>
	/// <returns><see langword="true"/> if <paramref name="remoteEndpoint"/>
	/// is whitelisted; otherwise <see langword="false"/>.</returns>
	public delegate bool ConnectionWhitelist(IPEndPoint remoteEndpoint);
	public class SocketServer
	{
		internal static readonly Logging.LogWrapper log = new Logging.LogWrapper();
		private readonly ParameterlessDelegate logConnectionClosed; //frequency bound
		protected Byte[] emptyReplyBytes = { 241, 216, 255, 255 };
		protected Byte[] serverCapabilityBytes = { 0, 1 };
		protected MemoryStream emptyReplyStream, serverCapabilityStream;

		protected ConnectionList connections;
		protected Byte[] emptyMessage = new Byte[] { };
		protected Byte[] trueBytes;
		protected Byte[] falseBytes;

		protected Timer connectionWatcher;
		protected Socket listener;
		protected BinaryFormatter messageObjectFormatter = new BinaryFormatter();

		protected int SyncThreads;
		protected int OnewayThreads;
		protected Dispatcher OnewayDispatcher;
		protected Dispatcher SyncDispatcher;
		protected DispatcherQueue OnewayMessageQueue;
		protected DispatcherQueue SyncMessageQueue;
		protected Port<ProcessState> OnewayMessagePort = new Port<ProcessState>();
		protected Port<ProcessState> SyncMessagePort = new Port<ProcessState>();

		protected Thread timerThread; //used to increment the avg timer. Since everything else is multithreaded this needs its own thread
		protected AsyncCallback acceptCallBack;
		protected AsyncCallback receiveCallBack;

		protected bool useNetworkOrder;

		protected int initialMessageSize = 1024;
		protected int maximumMessageSize = 20480;
		protected bool discardTooBigMessages;

		protected byte[] messageTerminatorBytesHost = BitConverter.GetBytes(Int16.MinValue);
		protected byte[] messageStarterBytesHost = BitConverter.GetBytes(Int16.MaxValue);
		protected byte[] messageTerminatorBytesNetwork = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(Int16.MinValue));
		protected byte[] messageStarterBytesNetwork = BitConverter.GetBytes(IPAddress.HostToNetworkOrder(Int16.MaxValue));

		protected WaitCallback processCall;

		protected MemoryStreamPool bufferPool;
		protected ResourcePool<ConnectionState> connectionStatePool;
		protected ResourcePool<ConnectionState>.BuildItemDelegate buildConnectionStateDelegate;
		protected ResourcePool<ConnectionState>.ResetItemDelegate resetConnectionStateDelegate;

		protected AsyncCallback replyCallBack;

		private bool sendSeverCapabilities;
		private const short ServerCapabilitiesRequestCommandId = Int16.MinValue; 
		private const short SendAckMessageId = Int16.MinValue;

		private SocketServerConfig config;
		private int maximumSockets;

		public delegate bool AcceptNewConnectionDelegate();
		public delegate bool AcceptNewRequestDelegate();
		protected AcceptNewConnectionDelegate acceptNewConnectionDelegate;
		protected AcceptNewRequestDelegate acceptNewRequestDelegate;

		private static bool DefaultAcceptDelegate() { return true; }

		/// <summary>
		/// Create a new socket server listening on portNumber
		/// </summary>
		/// <param name="portNumber">The port to listen on</param>
		public SocketServer(int portNumber)
		{
			this.portNumber = portNumber;
			acceptCallBack = AcceptCallBack;
			receiveCallBack = ReceiveCallBack;
			replyCallBack = SendReplyCallback;

			emptyReplyStream = new MemoryStream(4);
			emptyReplyStream.Write(emptyReplyBytes, 0, 4);
			emptyReplyStream.Seek(0, SeekOrigin.Begin);

			serverCapabilityStream = new MemoryStream(serverCapabilityBytes.Length);
			serverCapabilityStream.Write(serverCapabilityBytes, 0, serverCapabilityBytes.Length);
			serverCapabilityStream.Seek(0, SeekOrigin.Begin);

			trueBytes = BitConverter.GetBytes(true);
			falseBytes = BitConverter.GetBytes(false);

			acceptNewConnectionDelegate = DefaultAcceptDelegate;
			acceptNewRequestDelegate = DefaultAcceptDelegate;
			logConnectionClosed = Algorithm.FrequencyBoundMethod(timesSuppressed => log.WarnFormat("New Connection Closed. Server not accepting new connections. Mostly likely due to too many open sockets or pending requests. (Frequency Bound Log Message 1sec {0} logs suppressed)", timesSuppressed), TimeSpan.FromSeconds(1));
		}

		/// <summary>
		/// Create a new socket server named instanceName, listening on portNumber
		/// </summary>
		/// <param name="instanceName">The name of the socket server instance for performance data</param>
		/// <param name="portNumber">The port to listen on</param>
		public SocketServer(string instanceName, int portNumber)
			: this(portNumber)
		{
			this.instanceName = instanceName;
		}

		public AcceptNewConnectionDelegate AcceptingConnectionsDelegate
		{
			set
			{
				if (value == null)
				{
					acceptNewConnectionDelegate = new AcceptNewConnectionDelegate(DefaultAcceptDelegate);
				}
				else
				{
					acceptNewConnectionDelegate = value;
				}
			}
		}

		public AcceptNewRequestDelegate AcceptingRequestsDelegate
		{
			set
			{
				if (value == null)
				{
					acceptNewRequestDelegate = DefaultAcceptDelegate;
				}
				else
				{
					acceptNewRequestDelegate = value;
				}
			}
			get
			{
				return acceptNewRequestDelegate;
			}
		}

		private ConnectionWhitelist connectionWhitelist;

		private readonly object whitelistOnlyLock = new object();
		private bool whitelistOnly;

		/// <summary>
		/// Gets or sets whether only whitelisted connections are allowed.
		/// </summary>
		/// <value><para>A <see cref="Boolean"/> indicating whether only whitelisted
		/// connections are allowed.</para>
		/// <para>If <see langword="true"/> then incoming
		/// connections must be whitelisted to be accepted. Also, if the value
		/// is changed from <see langword="false"/> to <see langword="true"/>
		/// then existing connections are scanned and not whitelisted connections
		/// are closed.</para>
		/// <para>If <see cref="ConnectionWhitelist"/> is <see langword="null"/>
		/// then <see cref="WhitelistOnly"/> is ignored.</para>
		/// </value>
		public bool WhitelistOnly
		{
			get { return whitelistOnly; }
			set
			{
				lock (whitelistOnlyLock)
				{
					if (value == whitelistOnly) return;
					whitelistOnly = value;
					if (whitelistOnly)
					{
						// iterate over existing connections for whitelist
						var whitelist = connectionWhitelist;
						if (whitelist != null)
						{
							connections.PurgeNotWhitelisted(whitelist);
						}
					}
				}
			}
		}

		/// <summary>
		/// Gets the number of active connections.
		/// </summary>
		/// <value>The number of active connections.</value>
		public int ConnectionCount
		{
			get { return connections.SocketCount; }
		}

		#region Performance Counters

		public static readonly string PerformanceCategoryName = "MySpace.SocketServer";
		//The following three values must be coordinated with each other, and there must be a corresponding PerfomanceCounter for each!
		public static readonly string[] PerformanceCounterNames =
		{
			"Socket Count",
			"Connections Per Sec",
			"Msgs Per Sec - Sync",
			"Msgs Per Sec - One Way",
			"Buffers Allocated",
			"Requests Queued",
			"Avg Handler Time",
			"Avg Handler Time Base",
			"Free Worker Threads",
			"Free Completion Threads",
			"Active Worker Threads",
			"Active Completion Port Threads"
		};
		public static readonly string[] PerformanceCounterHelp =
		{
			"The number of currently open client sockets.",
			"The number of new connections per second.",
			"The number of syncronous messages per second.",
			"The number of one way messages per second.",
			"The number of buffers allocated in all buffer pools.",
			"The number of work items that have been queued and not yet dequeued.",
			"Average amount of time spend in the message handler.",
			"Base for average time.",
			"The number of free worker threads available.",
			"The number of free completion port threads available.",
			"The number of active worker threads.",
			"The number of active completion port threads."
		};
		public static readonly PerformanceCounterType[] PerformanceCounterTypes =
		{
			PerformanceCounterType.NumberOfItems32,
			PerformanceCounterType.RateOfCountsPerSecond32,
			PerformanceCounterType.RateOfCountsPerSecond32,
			PerformanceCounterType.RateOfCountsPerSecond32,
			PerformanceCounterType.NumberOfItems32,
			PerformanceCounterType.NumberOfItems32,
			PerformanceCounterType.AverageTimer32,
			PerformanceCounterType.AverageBase,
			PerformanceCounterType.NumberOfItems32,
			PerformanceCounterType.NumberOfItems32,
			PerformanceCounterType.NumberOfItems32,
			PerformanceCounterType.NumberOfItems32
		};

		protected PerformanceCounter socketCountCounter;
		protected PerformanceCounter connectionsPerSecCounter;
		protected PerformanceCounter syncPerSecCounter;
		protected PerformanceCounter onewayPerSecCounter;

		protected PerformanceCounter allocatedBuffers;
		protected PerformanceCounter requestsQueued;

		protected PerformanceCounter avgHandlerTime;
		protected PerformanceCounter avgHandlerTimeBase;

		protected PerformanceCounter freeWorkerThreadCounter;
		protected PerformanceCounter freeCompletionThreadCounter;

		protected PerformanceCounter activeWorkerThreadCounter;
		protected PerformanceCounter activeCompletionThreadCounter;

		protected string instanceName;

		public string InstanceName
		{
			get { return instanceName; }
		}

		protected bool countersInitialized;
		#endregion

		protected int connectionCheckInterval = 60000;

		protected bool isRunning;
		public bool IsRunning
		{
			get { return isRunning; }
		}

		readonly int portNumber;
		public int PortNumber
		{
			get { return portNumber; }
		}

		private IMessageHandler messageHandler;

		public IMessageHandler MessageHandler
		{
			get
			{
				return messageHandler;
			}
			set
			{
				messageHandler = value;
				asyncMessageHandler = value as IAsyncMessageHandler;
			}
		}

		public HandleAsyncMessage AsynMessageHandler;
		private IAsyncMessageHandler asyncMessageHandler;
		public bool UseDefaultReplyHeader = true;

		public void ReloadConfig(object sender, EventArgs args)
		{
			if (log.IsInfoEnabled)
				log.Info("Reloading config");
			connectionWatcher.Change(Timeout.Infinite, Timeout.Infinite);
			SocketServerConfig newConfig = ConfigurationManager.GetSection(_configSectionName) as SocketServerConfig;
			if (newConfig == null)
			{
				if (log.IsWarnEnabled)
					log.Warn("No Socket Server Config Found. Using defaults.");
				newConfig = new SocketServerConfig();
			}
			SetConfig(newConfig);
		}

		const string _configSectionName = "SocketServerConfig";

		/// <summary>
		///	<para>The configuration section name for the <see cref="SocketServerConfig"/>.</para>
		/// </summary>
		public static readonly string ConfigSectionName = _configSectionName;

		public SocketServerConfig GetCurrentConfig()
		{
			return config;
		}

		public void SetConfig(SocketServerConfig newConfig)
		{
			maximumMessageSize = newConfig.MaximumMessageSize;
			discardTooBigMessages = newConfig.DiscardTooBigMessages;
			connectionCheckInterval = newConfig.ConnectionCheckIntervalSeconds;
			useNetworkOrder = newConfig.UseNetworkOrder;
			maximumSockets = (newConfig.MaximumOpenSockets == 0 ? Int32.MaxValue : newConfig.MaximumOpenSockets);
			sendSeverCapabilities = newConfig.SendServerCapabilities;
			int bufferInitMsgSize = newConfig.InitialMessageSize;
			int bufferPoolReuses = newConfig.BufferPoolReuses;
			int connectionStateReuses = newConfig.ConnectionStateReuses;

			int currentWorker, currentCompletion, oldWorker, oldCompletion;
			ThreadPool.GetAvailableThreads(out currentWorker, out currentCompletion);
			oldWorker = currentWorker;
			oldCompletion = currentCompletion;
			if (config.MaximumWorkerThreads > 0) currentWorker = config.MaximumWorkerThreads;
			if (config.MaximumCompletionPortThreads > 0) currentCompletion = config.MaximumCompletionPortThreads;
			bool maxSet = ThreadPool.SetMaxThreads(currentWorker, currentCompletion);

			if (config.MaximumWorkerThreads > 0 || config.MaximumCompletionPortThreads > 0)
			{
				if (maxSet == false)
				{
					if (log.IsWarnEnabled)
					{
						log.WarnFormat("FAILED to change max ThreadPool threads from ({0}worker/{1}completion) to ({2}worker/{3}completion)", oldWorker, oldCompletion, currentWorker, currentCompletion);
					}
				}
				else
				{
					if (log.IsInfoEnabled) log.InfoFormat("Successfully changed max ThreadPool threads from ({0}worker/{1}completion) to ({2}worker/{3}completion)", oldWorker, oldCompletion, currentWorker, currentCompletion);
				}
			}

			Interlocked.Exchange(ref connectionCheckInterval, newConfig.ConnectionCheckIntervalSeconds * 1000);

			if (log.IsInfoEnabled)
			{
				log.InfoFormat("Setting default msg size to {0}. Setting connection check interval to {1} ms.", bufferInitMsgSize, connectionCheckInterval);
				log.InfoFormat("Maximum Message size is {0}. {1}.", maximumMessageSize.ToString("N0"),
					 (discardTooBigMessages) ? "Discarding too big messages" : "Immediately disposing too big message buffers");
			}
			bufferPool.InitialBufferSize = bufferInitMsgSize;
			bufferPool.BufferReuses = bufferPoolReuses;
			connectionStatePool.MaxItemReuses = connectionStateReuses;

			connectionWatcher.Change(connectionCheckInterval, connectionCheckInterval);

			int newOnewayThreads =
				(newConfig.OnewayThreads == 0 ? (Environment.ProcessorCount * Dispatcher.ThreadsPerCpu) : newConfig.OnewayThreads);
			if (newOnewayThreads != OnewayThreads)
			{
				try
				{
					if (log.IsInfoEnabled)
						log.InfoFormat("Changing number of oneway threads from {0} to {1}.", OnewayThreads, newOnewayThreads);
					Dispatcher oldOnewayDispatcher = OnewayDispatcher;
					DispatcherQueue oldOnewayQueue = OnewayMessageQueue;
					Dispatcher newOnewayDispatcher = new Dispatcher(newOnewayThreads, ThreadPriority.Normal, true, "Socket Server OneWay:" + portNumber.ToString());
					DispatcherQueue newOnewayQueue = new DispatcherQueue("Socket Server " + portNumber.ToString() + " one way", newOnewayDispatcher, TaskExecutionPolicy.ConstrainQueueDepthThrottleExecution, newConfig.OnewayQueueDepth);

					Interlocked.Exchange<Port<ProcessState>>(ref OnewayMessagePort, new Port<ProcessState>());

					Arbiter.Activate(newOnewayQueue,
						Arbiter.Receive<ProcessState>(true,
						OnewayMessagePort, ProcessCall));

					OnewayMessageQueue = newOnewayQueue;
					OnewayDispatcher = newOnewayDispatcher;
					oldOnewayDispatcher.Dispose();
					OnewayThreads = newOnewayThreads;
				}
				catch (Exception ex)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("Socket Server Exception changing number of sync threads: {0}", ex);
				}
			}

			int newSyncThreads = (newConfig.SyncThreads == 0 ?
				(Environment.ProcessorCount * Dispatcher.ThreadsPerCpu) : newConfig.SyncThreads);

			if (newSyncThreads != SyncThreads)
			{
				try
				{
					if (log.IsInfoEnabled)
						log.InfoFormat("Changing number of sync threads from {0} to {1}", SyncThreads, newSyncThreads);
					Dispatcher oldSyncDispatcher = SyncDispatcher;
					DispatcherQueue oldSyncQueue = SyncMessageQueue;
					Dispatcher newSyncDispatcher = new Dispatcher(newSyncThreads, ThreadPriority.AboveNormal, true, "Socket Server OneWay:" + portNumber.ToString());
					DispatcherQueue newSyncQueue = new DispatcherQueue("Socket Server " + portNumber.ToString() + " one way", newSyncDispatcher, TaskExecutionPolicy.ConstrainQueueDepthThrottleExecution, newConfig.SyncQueueDepth);

					Interlocked.Exchange<Port<ProcessState>>(ref SyncMessagePort, new Port<ProcessState>());

					Arbiter.Activate(newSyncQueue,
						Arbiter.Receive<ProcessState>(true,
						SyncMessagePort, delegate(ProcessState state) { ProcessCall(state); }));
					SyncMessageQueue = newSyncQueue;
					SyncDispatcher = newSyncDispatcher;
					oldSyncDispatcher.Dispose();
					SyncThreads = newSyncThreads;
				}
				catch (Exception ex)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("Socket Server Exception changing number of sync threads: {0}", ex);
				}
			}

			if (OnewayMessageQueue != null)
			{
				OnewayMessageQueue.MaximumQueueDepth = newConfig.OnewayQueueDepth;
			}
			if (SyncMessageQueue != null)
			{
				SyncMessageQueue.MaximumQueueDepth = newConfig.SyncQueueDepth;
			}

			config = newConfig;
		}
		
		private void GetConfig()
		{
			config = ConfigurationManager.GetSection(_configSectionName) as SocketServerConfig;

			if (config == null)
			{
				if (log.IsWarnEnabled)
					log.Warn("No Socket Server Config Found. Using defaults.");
				config = new SocketServerConfig();
			}

			useNetworkOrder = config.UseNetworkOrder;

			initialMessageSize = config.InitialMessageSize;
			maximumMessageSize = config.MaximumMessageSize;
			discardTooBigMessages = config.DiscardTooBigMessages;
			sendSeverCapabilities = config.SendServerCapabilities;

			maximumSockets = (config.MaximumOpenSockets == 0 ? Int32.MaxValue : config.MaximumOpenSockets);
			if (log.IsInfoEnabled)
			{
				log.InfoFormat("Maximum Message size is {0}. {1}.", maximumMessageSize.ToString("N0"),
					 (discardTooBigMessages) ? "Discarding too big messages" : "Immediately disposing too big message buffers");
			}

			if (config.SyncThreads > 0)
			{
				SyncThreads = config.SyncThreads;
			}
			else
			{
				SyncThreads = Environment.ProcessorCount * Dispatcher.ThreadsPerCpu;
			}

			if (config.OnewayThreads > 0)
			{
				OnewayThreads = config.OnewayThreads;
			}
			else
			{
				OnewayThreads = Environment.ProcessorCount * Dispatcher.ThreadsPerCpu;
			}

			XmlSerializerSectionHandler.RegisterReloadNotification(typeof(SocketServerConfig), ReloadConfig);
		}

		/// <summary>
		/// Start listening for connections.
		/// </summary>
		/// <param name="connectionWhiteList">An optional <see cref="ConnectionWhitelist"/>
		/// used to evaluate whether incoming and existing connections are
		/// whitelisted.</param>
		public void Start(ConnectionWhitelist connectionWhiteList)
		{
			if (isRunning)
			{
				if (log.IsWarnEnabled)
					log.Warn("Attempt to start already running socket server on port " + portNumber);
				return;
			}

			this.connectionWhitelist = connectionWhiteList;

			InitializeCounters();

			GetConfig();

			if (log.IsInfoEnabled)
				log.InfoFormat("Using a memory stream pool with an initial size of {0} bytes, reusing buffers {1} times. Reusing connectionstates {2} times. Using {3} sync threads and {4} one way threads",
				config.InitialMessageSize, config.BufferPoolReuses, config.ConnectionStateReuses, SyncThreads, OnewayThreads);

			bufferPool = new MemoryStreamPool(config.InitialMessageSize, config.BufferPoolReuses);

			if (countersInitialized)
			{
				bufferPool.AllocatedItemsCounter = this.allocatedBuffers;
			}

			buildConnectionStateDelegate = new ResourcePool<ConnectionState>.BuildItemDelegate(BuildConnectionState);
			resetConnectionStateDelegate = new ResourcePool<ConnectionState>.ResetItemDelegate(ResetConnectionState);

			connectionStatePool = new ResourcePool<ConnectionState>(buildConnectionStateDelegate, resetConnectionStateDelegate);
			connectionStatePool.MaxItemReuses = config.ConnectionStateReuses;

			listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

			connections = new ConnectionList(socketCountCounter);
			connectionCheckInterval = config.ConnectionCheckIntervalSeconds * 1000;
			connectionWatcher = new Timer(new TimerCallback(CheckConnections), null, connectionCheckInterval, connectionCheckInterval);

			int currentWorker, currentCompletion, oldWorker, oldCompletion;
			ThreadPool.GetAvailableThreads(out currentWorker, out currentCompletion);
			oldWorker = currentWorker;
			oldCompletion= currentCompletion;
			if (config.MaximumWorkerThreads > 0) currentWorker = config.MaximumWorkerThreads;
			if (config.MaximumCompletionPortThreads > 0) currentCompletion = config.MaximumCompletionPortThreads;
			bool maxSet = ThreadPool.SetMaxThreads(currentWorker, currentCompletion);

			if (config.MaximumWorkerThreads > 0 || config.MaximumCompletionPortThreads > 0)
			{
				if (maxSet == false)
				{
					if (log.IsWarnEnabled)
					{
						log.WarnFormat("FAILED to change max ThreadPool threads from ({0}worker/{1}completion) to ({2}worker/{3}completion)", oldWorker, oldCompletion, currentWorker, currentCompletion);
					}
				}
				else
				{
					if (log.IsInfoEnabled) log.InfoFormat("Successfully changed max ThreadPool threads from ({0}worker/{1}completion) to ({2}worker/{3}completion)", oldWorker, oldCompletion, currentWorker, currentCompletion);
				}
			}

			//kick off CCR
			OnewayDispatcher = new Dispatcher(OnewayThreads, ThreadPriority.Normal, true, "Socket Server OneWay:" + portNumber.ToString());
			SyncDispatcher = new Dispatcher(SyncThreads, ThreadPriority.AboveNormal, true, "Socket Server Sync:" + portNumber.ToString());
			OnewayMessageQueue = new DispatcherQueue("Socket Server " + portNumber.ToString() + " one way", OnewayDispatcher, TaskExecutionPolicy.Unconstrained, config.OnewayQueueDepth);
			SyncMessageQueue = new DispatcherQueue("Socket Server " + portNumber.ToString() + " sync", SyncDispatcher, TaskExecutionPolicy.Unconstrained, config.SyncQueueDepth);

			Arbiter.Activate(OnewayMessageQueue,
				Arbiter.Receive<ProcessState>(true, OnewayMessagePort, ProcessCall));

			Arbiter.Activate(SyncMessageQueue,
				Arbiter.Receive<ProcessState>(true, SyncMessagePort, ProcessCall));

			listener.Bind(new IPEndPoint(IPAddress.Any, this.portNumber));

			listener.Listen(500);

			isRunning = true;

			listener.BeginAccept(acceptCallBack, listener);

			timerThread = new Thread(TimerStart);
			timerThread.IsBackground = true;
			timerThread.Start();

			if (log.IsInfoEnabled)
				log.Info("MySpace SocketTransport Server started on port " + this.portNumber);
		}
		/// <summary>
		/// Start listening for connections.
		/// </summary>
		public void Start()
		{
			Start(null);
		}

		#region Perf Counters
		protected void InitializeCounters()
		{
			try
			{
				if (!PerformanceCounterCategory.Exists(PerformanceCategoryName))
				{
					//category doesn't exist at all.
					InstallCounters();
				}

				instanceName = instanceName ?? "Port " + portNumber.ToString();

				socketCountCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[0], InstanceName, false);
				socketCountCounter.RawValue = 0;
				connectionsPerSecCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[1], InstanceName, false);
				syncPerSecCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[2], InstanceName, false);
				onewayPerSecCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[3], InstanceName, false);
				allocatedBuffers = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[4], InstanceName, false);
				allocatedBuffers.RawValue = 0;
				requestsQueued = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[5], InstanceName, false);
				requestsQueued.RawValue = 0;
				avgHandlerTime = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[6], InstanceName, false);
				avgHandlerTimeBase = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[7], InstanceName, false);
				avgHandlerTime.RawValue = 0;
				avgHandlerTimeBase.RawValue = 0;

				freeWorkerThreadCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[8], InstanceName, false);
				freeCompletionThreadCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[9], InstanceName, false);

				try
				{
					activeWorkerThreadCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[10], InstanceName, false);
					activeCompletionThreadCounter = new PerformanceCounter(SocketServer.PerformanceCategoryName, SocketServer.PerformanceCounterNames[11], InstanceName, false);
				}
				catch (Exception ex)
				{
					log.Error("Some new socket transport performance counters are not installed. Try re-installing them.", ex);
				}

				CountAvailableThreads();

				countersInitialized = true;
			}
			catch (Exception ex)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Error initializing counters: {0}", ex);
				countersInitialized = false;
			}
		}

		protected void CountAvailableThreads()
		{
			int workerThreads, completionPortThreads;
			int maxWorkerThreads, maxCompletePortThreads;

			ThreadPool.GetMaxThreads(out maxWorkerThreads, out maxCompletePortThreads);
			ThreadPool.GetAvailableThreads(out workerThreads, out completionPortThreads);

			freeCompletionThreadCounter.RawValue = completionPortThreads;
			freeWorkerThreadCounter.RawValue = workerThreads;

			try
			{
				if (activeWorkerThreadCounter != null)
				{
					activeWorkerThreadCounter.RawValue = maxWorkerThreads - workerThreads;
				}

				if (activeCompletionThreadCounter != null)
				{
					activeCompletionThreadCounter.RawValue = maxCompletePortThreads - completionPortThreads;
				}
			}
			catch (Exception ex)
			{
				log.Error("Some new socket transport performance counters are not installed. Try re-installing them.", ex);
				activeWorkerThreadCounter = null;
				activeCompletionThreadCounter = null;
			}
		}

		protected void InstallCounters()
		{
			countersInitialized = CounterInstaller.InstallCounters();
		}

		protected void RemoveCounters()
		{
			CounterInstaller.RemoveCounters();
			countersInitialized = false;
		}
		#endregion

		/// <summary>
		/// Stop listening for connections and dispose of resources
		/// </summary>
		public void Stop()
		{
			if (!isRunning)
			{
				return;
			}

			if (log.IsInfoEnabled)
				log.Info("Socket Server Shutting Down.");
			isRunning = false;

			if (log.IsInfoEnabled)
				log.Info("Socket Server Disconnecting Listener.");

			if (listener.Connected)
			{
				listener.Shutdown(SocketShutdown.Both);
				listener.Disconnect(false);
			}
			listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.DontLinger, true);
			listener.Close();


			if (countersInitialized)
			{
				if (requestsQueued.RawValue > 0) //give queued requests a chance to execute
				{
					for (int waits = 0; waits < 5; waits++)
					{
						Thread.Sleep(500);
						if (requestsQueued.RawValue == 0)
							break;
					}
				}
			}

			//try to get rid of all connections

			//kill the connection watcher timer loop
			if (connectionWatcher != null)
			{
				connectionWatcher.Change(Timeout.Infinite, Timeout.Infinite);
				connectionWatcher = null;
			}
			if (log.IsInfoEnabled)
				log.Info("Socket Server Closing Down Sockets.");

			connections.Purge();

			if (log.IsInfoEnabled)
				log.Info("Shutting down dispatchers.");

			OnewayDispatcher.Dispose();
			OnewayDispatcher = null;
			SyncDispatcher.Dispose();
			SyncDispatcher = null;

			if (countersInitialized)
			{
				socketCountCounter.RawValue = 0;
				allocatedBuffers.RawValue = 0;
			}

			connectionWhitelist = null;

			if (log.IsInfoEnabled)
				log.InfoFormat("MySpace SocketTransport Server stopped on port {0}", this.portNumber);

		}

		protected void TimerStart()
		{
			long lastReading;
			try
			{
				while (isRunning)
				{
					lastReading = Stopwatch.GetTimestamp();
					Thread.Sleep(1000);
					avgHandlerTime.IncrementBy(Stopwatch.GetTimestamp() - lastReading);
					CountAvailableThreads();
					CountQueuedTasks();
				}
			}
			catch (Exception exc)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Server Exception in socket server timer loop: {0}", exc);
			}
		}

		private void CountQueuedTasks()
		{
			if (countersInitialized && isRunning)
			{
				try
				{
					requestsQueued.RawValue = (SyncDispatcher.PendingTaskCount + OnewayDispatcher.PendingTaskCount);
				}
				catch (ObjectDisposedException) { } //just for shutdown. not worth doing real sync for it				
			}
		}

		protected void RemoveConnection(ConnectionState state)
		{
			connections.Remove(state);
		}

		protected void RemoveConnection(IPEndPoint endpoint)
		{
			connections.Remove(endpoint);
		}

		protected void AcceptCallBack(IAsyncResult ar)
		{
			try
			{
				Socket listener = (Socket)ar.AsyncState;
				Socket connection = null;
				
				try
				{
					connection = listener.EndAccept(ar);

					if (AcceptNewConnections() == false)
					{
						logConnectionClosed(); 
						connection.Close();
						return;
					}

					if (whitelistOnly && connectionWhitelist != null &&
						!connectionWhitelist((IPEndPoint)connection.RemoteEndPoint))
					{
						connection.Close();
						return;
					}

					if (countersInitialized)
					{
						connectionsPerSecCounter.Increment();
					}

				}
				catch (ObjectDisposedException)
				{
					return;
				}

				connection.ReceiveTimeout = config.ReceiveTimeout;
				connection.ReceiveBufferSize = config.ReceiveBufferSize;
				connection.SendTimeout = config.SendTimeout;
				connection.SendBufferSize = config.SendBufferSize;
				ResourcePoolItem<ConnectionState> connectionStateItem = connectionStatePool.GetItem();
				ConnectionState connectionState = connectionStateItem.Item;
				connectionState.WorkSocket = connection;
				connections.Add(connectionState);
				connection.BeginReceive(connectionState.networkBuffer, 0, connectionState.BufferSize, SocketFlags.None, receiveCallBack, connectionStateItem);
			}
			catch (SocketException sex)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Error {0} accepting connection.", sex.SocketErrorCode);
			}
			catch (Exception e)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Server Exception accepting connection: {0}", e);
			}
			finally
			{
				if (isRunning)
				{
					try
					{
						listener.BeginAccept(acceptCallBack, listener);
					}
					catch (Exception ex)
					{
						if (log.IsErrorEnabled)
							log.ErrorFormat("Socket Server Exception beginning another accept: {0}. Restarting service.", ex);
						Stop();
						Start();
					}
				}
			}
		}

		private bool AcceptNewConnections()
		{
			return connections.SocketCount < maximumSockets && acceptNewConnectionDelegate();
		}

		private ConnectionState BuildConnectionState()
		{
			return new ConnectionState(config.ReceiveBufferSize, initialMessageSize);
		}

		private void ResetConnectionState(ConnectionState state)
		{
			state.WorkSocket = null;
			state.ReplySocket = null;
			state.remoteEndPoint = null;
			ResetConnectionStateMessageBuffer(state);
		}

		private void ResetConnectionStateMessageBuffer(ConnectionState state)
		{
			if (state.messageSize > maximumMessageSize && !discardTooBigMessages)
			{
				state.messageBuffer.Dispose();
				state.messageBuffer = new MemoryStream(initialMessageSize);
			}
			else
			{
				state.messageBuffer.Seek(0, SeekOrigin.Begin);
				state.messageBuffer.SetLength(0);
			}
			state.messagePosition = 0;
			state.messageSize = -1;
		}


		protected bool CheckForMessageStarter(byte[] networkBuffer)
		{
			byte[] messageStarterBytes = (useNetworkOrder ? this.messageStarterBytesNetwork : this.messageStarterBytesHost);
			for (int i = 0; i < messageStarterBytes.Length; i++)
			{
				if (networkBuffer[i] != messageStarterBytes[i])
				{
					return false;
				}
			}
			return true;
		}

		protected bool CheckForMessageTerminator(byte[] buffer, int messageSize)
		{
			byte[] messageTerminatorBytes = (useNetworkOrder ? this.messageTerminatorBytesNetwork : this.messageTerminatorBytesHost);
			for (int i = 0; i < messageTerminatorBytes.Length; i++)
			{
				if (buffer[messageSize - messageTerminatorBytes.Length + i] != messageTerminatorBytes[i])
				{
					return false;
				}
			}
			return true;

		}

		protected void ReceiveCallBack(IAsyncResult ar)
		{
			ResourcePoolItem<ConnectionState> stateItem = ar.AsyncState as ResourcePoolItem<ConnectionState>;
			ConnectionState state = stateItem.Item;
			Socket connection = state.WorkSocket;
			if (connection == null) return;
			SocketError endError, beginError;
			bool processing = false;

			try
			{
				int bytesRead = connection.EndReceive(ar, out endError);

				if (endError == SocketError.Success && bytesRead > 0)
				{
					lock (state)
					{
						state.messageBuffer.Write(state.networkBuffer, 0, bytesRead);
						state.messagePosition += bytesRead;
						processing = true;
					}

					while (processing)
					{
						if (state.messageSize < 0 && state.messagePosition >= 2)
						{
							lock (state)
							{
								if (state.messageSize < 0)
								{
									if (CheckForMessageStarter(state.messageBuffer.GetBuffer()))
									{
										state.messageSize = 0; //found the beginning, can start looking for the rest...
									}
									else
									{
										if (log.IsWarnEnabled)
											log.WarnFormat("Expected message start, received other from {0}.  Waiting for next receive that starts with valid message start.", connection.RemoteEndPoint);
										ResetConnectionStateMessageBuffer(state);
									}
								}
							}
						}

						if (state.messageSize == 0 && state.messagePosition >= 6)
						{
							//we haven't yet determined a message size, but there's enough data 
							//to do so
							lock (state)
							{
								state.messageSize = GetMessageSize(state.messageBuffer);
								if (state.messageSize > maximumMessageSize)
								{
									if (log.IsWarnEnabled)
										log.WarnFormat("Message with size {0} from {1}. {2}.",
										state.messageSize.ToString("N0"),
										state.remoteEndPoint,
										discardTooBigMessages ? "Discarding data from this message." : "Message buffer will be disposed immediately after processing."
										);
								}
							}
						}

						if (state.messagePosition > 0
							&& state.messageSize > 0
							&& state.messagePosition >= state.messageSize)
						{
							if (state.messageSize > maximumMessageSize && discardTooBigMessages)
							{
								//we think we have enough info, but we discarded all of it
								lock (state)
								{
									ResetConnectionStateMessageBuffer(state);
								}
							}
							else
							{
								//we have enough data for a message
								lock (state)
								{
									if (HandleCompleteSentData(state) == false)
									{
										CloseConnection(stateItem);
										return;
									}
									if (state.messagePosition > state.messageSize)
									{
										//there's some of the next message in the buffer already
										int newPos = state.messagePosition - state.messageSize;
										byte[] buff = state.messageBuffer.GetBuffer();
										state.messageBuffer.Seek(0, SeekOrigin.Begin);
										state.messageBuffer.Write(buff, state.messageSize, state.messagePosition - state.messageSize);
										//now the portion in the buffer that was from the next message is a the beginning of the buffer
										state.messagePosition = newPos;
										state.messageSize = -1;
									}
									else
									{
										ResetConnectionStateMessageBuffer(state);
										processing = false;
									}
								}
							}
						}
						else
						{
							//we don't have enough for a message and have to go back to listening
							processing = false;
						}
					}

					if (connection.Connected)
					{
						connection.BeginReceive(state.networkBuffer, 0, state.BufferSize, SocketFlags.None, out beginError, receiveCallBack, stateItem);
						if (beginError != SocketError.Success)
						{
							if (log.IsErrorEnabled)
								log.ErrorFormat("Error beginning another receive: {0}", beginError);
							CloseConnection(stateItem);
						}
					}
					else
					{
						if (log.IsErrorEnabled)
							log.Error("Connection dropped during ReceiveCallBack");
					}
				}
				else if (endError == SocketError.Success) //Successful notififcation of 0 byte read - this means the client disconnected cleanly.
				{
					CloseConnection(stateItem);
				}
				else //endError != Success
				{
					if (endError != SocketError.ConnectionReset) //that just means the client had its app domain shut off; happens all the time.
					{
						if (log.IsErrorEnabled)
							log.ErrorFormat("Socket Error during EndReceive from {0}: {1}.", state.remoteEndPoint, endError);
					}
					CloseConnection(stateItem);
				}
			}
			catch (SocketException se)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Exception during ReceiveCallback: {0}. Removing Connection.", se.SocketErrorCode);
				try
				{
					CloseConnection(stateItem);
				}
				catch (Exception e)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("Socket Server Exception removing socket: {0}", e.ToString());
				}
			}
			catch (ObjectDisposedException)
			{
			}
		}

		private void CloseConnection(ResourcePoolItem<ConnectionState> stateItem)
		{
			ConnectionState state = stateItem.Item;
			Socket connection = state.WorkSocket;
			if (connection.Connected)
			{
				connection.Shutdown(SocketShutdown.Both);
				connection.Close();
			}
			RemoveConnection(state);
			connectionStatePool.ReleaseItem(stateItem);
		}

		private int GetMessageSize(MemoryStream memoryStream)
		{
			return GetHostOrdered(BitConverter.ToInt32(memoryStream.GetBuffer(), 2), useNetworkOrder);
		}

		/// <summary>
		/// Handles a complete message.
		/// </summary>
		/// <param name="state">The state.</param>
		/// <returns>Returns <see langword="false"/> we can't handle any more requests from this client.</returns>
		protected bool HandleCompleteSentData(ConnectionState state)
		{
			bool sendReply, sendAck;

			byte[] buff = state.messageBuffer.GetBuffer();
			short commandId = 0;
			short messageId;

			try
			{
				if (useNetworkOrder)
				{
					messageId = GetHostOrdered(BitConverter.ToInt16(buff, 6), true);
					commandId = GetHostOrdered(BitConverter.ToInt16(buff, 8), true);
				}
				else
				{
					commandId = BitConverter.ToInt16(buff, 6);
					messageId = BitConverter.ToInt16(buff, 8);
				}

				sendReply = BitConverter.ToBoolean(buff, 10);
				sendAck = (messageId == SendAckMessageId);
				if (countersInitialized)
				{
					if (sendReply)
						syncPerSecCounter.Increment();
					else
						onewayPerSecCounter.Increment();
				}
			}
			catch (Exception e)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Server Exception extracting message info from {0}: {1} . Resetting connection state.", state.remoteEndPoint, e);
				ResetConnectionStateMessageBuffer(state);
				return true;
			}

			if (!CheckForMessageTerminator(buff, state.messageSize))
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Message without end terminator found from {0}. Resetting connection state.", state.remoteEndPoint);

				ResetConnectionStateMessageBuffer(state);
				return true;
			}

			try
			{

				ResourcePoolItem<MemoryStream> messageBuffer = bufferPool.GetItem();
				try
				{
					messageBuffer.Item.Write(buff, 11, state.messageSize - 13);
					messageBuffer.Item.Seek(0, SeekOrigin.Begin);

					ReplyType replyType = ReplyType.None;
					if (sendAck)
						replyType = ReplyType.SendAck;
					if (sendReply)
					{
						if(sendAck)
							log.ErrorFormat("Received message with both send ack and send reply set from {0}. Ack will be sent but client will error out waiting for reply.", state.remoteEndPoint);
						replyType = ReplyType.SendReply;
					}

					ProcessState processState = new ProcessState(state.ReplySocket, commandId, messageId, replyType, messageBuffer,
					                                             state.messageSize - 13);

					if (sendReply)
					{
						if (SyncDispatcher.PendingTaskCount < config.SyncQueueDepth && AcceptingRequestsDelegate())
						{
							SyncMessagePort.Post(processState);
						}
						else
						{
							return false; //will cause connection to close
						}
					}
					else
					{
						if (OnewayDispatcher.PendingTaskCount < config.OnewayQueueDepth && AcceptingRequestsDelegate())
						{
							OnewayMessagePort.Post(processState);
						}
						else
						{
							return false; //will cause connection to close
						}
					}
				}
				catch (Exception ex)
				{

					if (log.IsErrorEnabled)
						log.ErrorFormat("Socket Server Exception enqueueing message work item for {0}: {1}. Releasing buffer.",
						                state.remoteEndPoint, ex);
					bufferPool.ReleaseItem(messageBuffer);
				}
			}
			catch (Exception ex)
			{

				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Server Exception while handling message for {0}: {1}. Resetting message state.", state.remoteEndPoint, ex);
				ResetConnectionStateMessageBuffer(state);
				return true;
			}

			return true;
		}

		protected void ProcessCall(ProcessState state)
		{
			MemoryStream replyStream = null;
			bool ackSuccessful = true; //default to true, because we don't always try to send one
			MessageState message = null;
			try
			{
				if (state.ReplyType == ReplyType.SendAck)
				{
					ackSuccessful = SendReply(state, null); //sendack is mutually exclusive with sendreply, so we just do this and then carry on. 
				}

				MemoryStream messageStream = state.Message.Item;

				if (sendSeverCapabilities && state.CommandId == ServerCapabilitiesRequestCommandId)
				{
				    replyStream = serverCapabilityStream;
				}
				else if(ackSuccessful) //we do not want to process the message if the sender thinks the ack failed
				{
					if (asyncMessageHandler != null)
					{
						message = new MessageState
						          	{
						          		CommandId = state.CommandId,
						          		Message = messageStream,
						          		Length = state.MessageLength,
						          		ClientIP = state.RemoteEndpoint
						          	};

						asyncMessageHandler.BeginHandleMessage(message, (asyncResult) =>
						                                                	{
						                                                		try
						                                                		{
						                                                			MemoryStream reply =
						                                                				asyncMessageHandler.EndHandleMessage(asyncResult);
						                                                			CompleteProcessCall(state, reply); //not APM
						                                                		}
						                                                		catch (Exception exc)
						                                                		{
						                                                			if (log.IsErrorEnabled)
						                                                				log.Error(exc);
						                                                		}
						                                                	});

						return; //very important
					}

					if (AsynMessageHandler != null)
					{
						replyStream = AsynMessageHandler.Invoke((int) state.CommandId, messageStream, state.RemoteEndpoint.Address);
					}
					else if (MessageHandler != null)
					{
						replyStream = MessageHandler.HandleMessage((int) state.CommandId, messageStream, state.MessageLength);
					}
				}
			}
			catch (Exception ex)
			{
				try
				{
					string endPoint = state.Socket.RemoteEndPoint.ToString();

					if (log.IsErrorEnabled)
						log.ErrorFormat("Socket Server Exception handling message from {0}: {1}.", endPoint, ex);
					replyStream = null;
				}
				catch (ObjectDisposedException) { }
			}
			finally
			{
				if (message != null)
				{
					message.Message = null;
					message.Length = 0;
				}
				bufferPool.ReleaseItem(state.Message);
				state.Message = null;
			}

			CompleteProcessCall(state, replyStream);
		}

		private void CompleteProcessCall(ProcessState state, MemoryStream replyStream)
		{
			if (countersInitialized)
			{
				avgHandlerTimeBase.Increment();
			}

			if (state.ReplyType == ReplyType.SendReply)
			{
				SendReply(state, replyStream);
			}
		}

		private bool SendReply(ProcessState state, MemoryStream replyStream)
		{
			int replyLength;
			if (replyStream == null)
			{
				replyStream = emptyReplyStream;
				replyLength = 4;
			}
			else
			{
				replyLength = (int)replyStream.Length;
			}
			return SendReply(state, replyStream, replyLength);
		}

		protected bool SendReply(ProcessState state, MemoryStream reply, int replyLength)
		{
			SocketError socketError;
			try
			{
				int replySize = replyLength + 4;
				if (state.MessageId != 0) //if the client sent a message Id, we need to send it back
				{
					replySize += 2;
				}

				state.ReplyBuffer = bufferPool.GetItem();
				if (state.ReplyBuffer.Item.Position > 0)
				{

					if (log.IsErrorEnabled)
						log.ErrorFormat("Buffer from pool had position {0}!. Resetting position and length.", state.ReplyBuffer.Item.Position.ToString("N0"));
					state.ReplyBuffer.Item.Seek(0, SeekOrigin.Begin);
					state.ReplyBuffer.Item.SetLength(0);
				}
				if (UseDefaultReplyHeader)
				{
					state.ReplyBuffer.Item.Write(BitConverter.GetBytes(GetNetworkOrdered(replySize, useNetworkOrder)), 0, 4);
					if (state.MessageId != 0)
					{
						state.ReplyBuffer.Item.Write(BitConverter.GetBytes(GetNetworkOrdered(state.MessageId, useNetworkOrder)), 0, 2);
					}
					state.ReplyBuffer.Item.Write(reply.GetBuffer(), 0, replyLength);
				}
				
				if (state.Socket.Connected)
				{
					state.Socket.BeginSend(state.ReplyBuffer.Item.GetBuffer(), 0, replySize, SocketFlags.None, out socketError, replyCallBack, state);
					if (socketError != SocketError.Success)
					{
						if (log.IsErrorEnabled)
							log.ErrorFormat("Error sending reply to {0}: {1}.", state.RemoteEndpoint, socketError);
						if (!state.Socket.Connected)
						{
							state.Socket.Shutdown(SocketShutdown.Both);
							state.Socket.Close();
						}
						RemoveConnection(state.RemoteEndpoint);
						return false;
					}
					return true;
				}
				else
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("Connection dropped before reply sent to {0}", state.RemoteEndpoint);
					bufferPool.ReleaseItem(state.ReplyBuffer);
					state.ReplyBuffer = null;
					return false;
				}
			}
			catch (SocketException ex)
			{
				if (state.ReplyBuffer != null)
				{
					bufferPool.ReleaseItem(state.ReplyBuffer);
					state.ReplyBuffer = null;
				}

				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Exception during SendReply to {0}: {1}.  Removing connection.", state.RemoteEndpoint, ex);
				try
				{
					if (state.Socket.Connected)
					{
						state.Socket.Shutdown(SocketShutdown.Both);
						state.Socket.Close();
					}
					RemoveConnection(state.RemoteEndpoint);
				}
				catch (Exception exc)
				{

					if (log.IsErrorEnabled)
						log.ErrorFormat("Socket Server Exception attempted to remove connection in SendReply exception cleanup: {0}", exc);
				}
				return false;
			}
			catch (ObjectDisposedException)
			{
				if (state.ReplyBuffer != null)
				{
					bufferPool.ReleaseItem(state.ReplyBuffer);
					state.ReplyBuffer = null;
				}
				return false;
			}
			catch (Exception ex)
			{
				if (state.ReplyBuffer != null)
				{
					bufferPool.ReleaseItem(state.ReplyBuffer);
					state.ReplyBuffer = null;
				}

				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Server Exception during SendReply to {0}: {1}.", state.RemoteEndpoint, ex);
				
				return false;
			}
		}

		/// <summary>
		/// Sends the <paramref name="messageStream"/> response for an existing request
		/// represented by <paramref name="responseState"/>.
		/// </summary>
		/// <param name="responseState">The state object that represents the existing connection.</param>
		/// <param name="messageStream">The <see cref="MemoryStream"/> to send.</param>
		/// <exception cref="ArgumentException">Thrown when <paramref name="responseState"/>
		/// is not an instance of <see cref="ProcessState"/>.</exception>
		public void SendResponse(object responseState, MemoryStream messageStream)
		{
			ProcessState state = responseState as ProcessState;
			if (state == null)
			{
				if (log.IsErrorEnabled)
					log.Error("SocketServer: An attempt was made to SendResponse without a responseState.");
				throw new ArgumentException("responseState must be an instance of ProcessState", "responseState");
			}

			if (countersInitialized)
			{
				avgHandlerTimeBase.Increment();
			}

			SendReply(state, messageStream);
		}

		protected void SendReplyCallback(IAsyncResult ar)
		{
			ProcessState state = ar.AsyncState as ProcessState;
			try
			{
				if (state.Socket.Connected)
				{
					state.Socket.EndSend(ar);
				}
			}
			catch (SocketException ex)
			{

				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Exception during SendReplyCallback to {0}: {1}. Removing connection.", state.RemoteEndpoint, ex);
				try
				{
					if (state.Socket.Connected)
					{
						state.Socket.Shutdown(SocketShutdown.Both);
						state.Socket.Close();
					}
					RemoveConnection(state.RemoteEndpoint);
				}
				catch (Exception exc)
				{
					if (log.IsErrorEnabled)
						log.ErrorFormat("Socket Server Exception attempted to remove connection in SendReplyCallback exception cleanup: {0}", exc);
				}
			}
			catch (ObjectDisposedException)
			{
				if (log.IsErrorEnabled)
					log.Error("Object disposed exception in SendReplyCallback");
			}
			finally
			{
				bufferPool.ReleaseItem(state.ReplyBuffer);
				state.ReplyBuffer = null;
			}
		}

		public void CheckConnections(object state)
		{
			log.Debug("Starting Connection Check");

			connectionWatcher.Change(Timeout.Infinite, Timeout.Infinite);

			try
			{
				connections.CheckConnections();
			}
			catch (Exception ex)
			{
				if (log.IsErrorEnabled)
					log.ErrorFormat("Socket Server Exception while checking SocketServer connections: {0}", ex);
			}
			finally
			{
				if (isRunning)
				{
					connectionWatcher.Change(connectionCheckInterval, connectionCheckInterval);
				}
			}
		}

		#region Network Ordering Methods

		private Int16 GetNetworkOrdered(Int16 number, bool useNetworkOrder)
		{
			if (useNetworkOrder)
			{
				return IPAddress.HostToNetworkOrder(number);
			}
			else
			{
				return number;
			}
		}

		private Int32 GetNetworkOrdered(Int32 number, bool useNetworkOrder)
		{
			if (useNetworkOrder)
			{
				return IPAddress.HostToNetworkOrder(number);
			}
			else
			{
				return number;
			}
		}

		private Int64 GetNetworkOrdered(Int64 number, bool useNetworkOrder)
		{
			if (useNetworkOrder)
			{
				return IPAddress.HostToNetworkOrder(number);
			}
			else
			{
				return number;
			}
		}

		private Int16 GetHostOrdered(Int16 number, bool useNetworkOrder)
		{
			if (useNetworkOrder)
			{
				return IPAddress.NetworkToHostOrder(number);
			}
			else
			{
				return number;
			}
		}

		private Int32 GetHostOrdered(Int32 number, bool useNetworkOrder)
		{
			if (useNetworkOrder)
			{
				return IPAddress.NetworkToHostOrder(number);
			}
			else
			{
				return number;
			}
		}

		private Int64 GetHostOrdered(Int64 number, bool useNetworkOrder)
		{
			if (useNetworkOrder)
			{
				return IPAddress.NetworkToHostOrder(number);
			}
			else
			{
				return number;
			}
		}

		#endregion
	}
}
