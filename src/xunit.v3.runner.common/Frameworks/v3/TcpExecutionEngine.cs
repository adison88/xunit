using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit.Internal;
using Xunit.Runner.Common;

namespace Xunit.v3
{
	/// <summary>
	/// The execution-side engine used to host an xUnit.net v3 test assembly that communicates via
	/// TCP to the remote side, which is running <see cref="TcpRunnerEngine"/>. After connecting to
	/// the TCP port, responds to commands from the runner engine, which translate/ to commands on
	/// the <see cref="_ITestFrameworkDiscoverer"/> and <see cref="_ITestFrameworkExecutor"/>.
	/// </summary>
	public class TcpExecutionEngine : IAsyncDisposable
	{
		readonly BufferedTcpClient bufferedClient;
		readonly HashSet<string> cancellationRequested = new HashSet<string>();
		readonly List<(byte[] command, Action<ReadOnlyMemory<byte>?> handler)> commandHandlers = new();
		readonly _IMessageSink diagnosticMessageSink;
		readonly string engineID;
		readonly XunitFilters filters;
		readonly int port;
		readonly TaskCompletionSource<int> shutdownRequested = new TaskCompletionSource<int>();
		readonly Socket socket;

		/// <summary>
		/// Initializes a new instance of the <see cref="TcpExecutionEngine"/> class.
		/// </summary>
		/// <param name="engineID">Engine ID (used for diagnostic messages).</param>
		/// <param name="port">The TCP port to connect to (localhost is assumed).</param>
		/// <param name="filters">Filters to be applied while discovering and running tests.</param>
		/// <param name="diagnosticMessageSink">The message sink to send diagnostic messages to.</param>
		public TcpExecutionEngine(
			string engineID,
			int port,
			XunitFilters filters,
			_IMessageSink diagnosticMessageSink)
		{
			commandHandlers.Add((TcpEngineMessages.Runner.Cancel, OnCancel));
			commandHandlers.Add((TcpEngineMessages.Runner.Quit, OnQuit));

			this.engineID = Guard.ArgumentNotNull(nameof(engineID), engineID);
			this.port = port;
			this.filters = Guard.ArgumentNotNull(nameof(filters), filters);
			this.diagnosticMessageSink = Guard.ArgumentNotNull(nameof(diagnosticMessageSink), diagnosticMessageSink);

			socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
			bufferedClient = new BufferedTcpClient($"execution::{engineID}", socket, ProcessRequest, diagnosticMessageSink);
		}

		/// <inheritdoc/>
		public async ValueTask DisposeAsync()
		{
			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Disconnecting from tcp://localhost:{port}/" });

			await bufferedClient.DisposeAsync();

			socket.Shutdown(SocketShutdown.Receive);
			socket.Shutdown(SocketShutdown.Send);
			socket.Close();
			socket.Dispose();

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Disconnected from tcp://localhost:{port}/" });
		}

		void OnCancel(ReadOnlyMemory<byte>? data)
		{
			if (!data.HasValue || data.Value.Length == 0)
				diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): CANCEL data is missing the operation ID" });
			else
			{
				var operationID = Encoding.UTF8.GetString(data.Value.ToArray());
				cancellationRequested.Add(operationID);
			}
		}

		void OnQuit(ReadOnlyMemory<byte>? _) =>
			shutdownRequested.TrySetResult(0);

		void ProcessRequest(ReadOnlyMemory<byte> request)
		{
			var (command, data) = TcpEngineMessages.SplitOnSeparator(request);

			foreach (var commandHandler in commandHandlers)
				if (command.Span.SequenceEqual(commandHandler.command))
				{
					commandHandler.handler(data);
					return;
				}

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Received unknown command: {Encoding.UTF8.GetString(command.ToArray())}" });
		}

		/// <summary>
		/// Sends <see cref="TcpEngineMessages.Execution.Message"/>.
		/// </summary>
		/// <param name="operationID">The operation ID that this message is for.</param>
		/// <param name="message">The message to be sent.</param>
		/// <returns>Returns <c>true</c> if the operation should continue to run tests; <c>false</c> if it should cancel the run.</returns>
		public bool SendMessage(
			string operationID,
			_MessageSinkMessage message)
		{
			Guard.ArgumentNotNull(nameof(operationID), operationID);
			Guard.ArgumentNotNull(nameof(message), message);

			bufferedClient.Send(TcpEngineMessages.Execution.Message);
			bufferedClient.Send(TcpEngineMessages.Separator);
			bufferedClient.Send(operationID);
			bufferedClient.Send(TcpEngineMessages.Separator);
			bufferedClient.Send(message.ToJson());
			bufferedClient.Send(TcpEngineMessages.EndOfMessage);

			return !cancellationRequested.Contains(operationID);
		}

		/// <summary>
		/// Starts the execution engine, connecting back to the runner engine on the TCP port
		/// provided to the constructor.
		/// </summary>
		/// <returns>The local port used for the conection.</returns>
		public async ValueTask<int> Start()
		{
			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Connecting to tcp://localhost:{port}/" });

			await socket.ConnectAsync(IPAddress.Loopback, port);
			bufferedClient.Start();

			diagnosticMessageSink.OnMessage(new _DiagnosticMessage { Message = $"TcpExecutionEngine({engineID}): Connected to tcp://localhost:{port}/" });

			return ((IPEndPoint?)socket.LocalEndPoint)?.Port ?? throw new InvalidOperationException("Could not retrieve port from socket local endpoint");
		}

		/// <summary>
		/// Waits for the QUIT signal from the runner engine.
		/// </summary>
		// TODO: CancellationToken? Timespan for timeout?
		public Task WaitForQuit() =>
			shutdownRequested.Task;
	}
}
