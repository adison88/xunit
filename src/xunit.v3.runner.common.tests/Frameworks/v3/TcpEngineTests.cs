﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using Xunit.Runner.Common;
using Xunit.v3;

public class TcpEngineTests
{
	public class StartupAndShutdownMessages
	{
		readonly XunitFilters emptyFilters = new();
		readonly List<_MessageSinkMessage> messages = new();
		readonly _NullMessageSink nullMessageSink = new();
		readonly _IMessageSink spyMessageSink;

		public StartupAndShutdownMessages()
		{
			spyMessageSink = SpyMessageSink.Create(messages: messages);
		}

		[Fact]
		public async ValueTask RunnerEngine_NoConnection()
		{
			var runnerEngine = new TcpRunnerEngine("1r", nullMessageSink, spyMessageSink);
			var port = runnerEngine.Start();

			await runnerEngine.DisposeAsync();

			Assert.Collection(
				messages.OfType<_DiagnosticMessage>().Select(dm => dm.Message),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Listening on tcp://localhost:{port}/", msg)
			);
		}

		[Fact]
		public async ValueTask RunnerEngine_WithConnection_CleanShutdown()
		{
			var runnerEngine = new TcpRunnerEngine("1r", nullMessageSink, spyMessageSink);
			var runnerPort = runnerEngine.Start();
			var executionEngine = new TcpExecutionEngine("1e", runnerPort, emptyFilters, nullMessageSink);
			var executionPort = await executionEngine.Start();
			await WaitForConnection(runnerEngine);

			await runnerEngine.DisposeAsync();
			await WaitForQuit(executionEngine);
			await executionEngine.DisposeAsync();

			Assert.Collection(
				messages.OfType<_DiagnosticMessage>().Select(dm => dm.Message),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Listening on tcp://localhost:{runnerPort}/", msg),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Connection accepted from tcp://localhost:{executionPort}/", msg),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Disconnecting from tcp://localhost:{executionPort}/", msg),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Disconnected from tcp://localhost:{executionPort}/", msg)
			);
		}

		[Fact]
		public async ValueTask RunnerEngine_WithConnection_UncleanShutdown()
		{
			var runnerEngine = new TcpRunnerEngine("1r", nullMessageSink, spyMessageSink);
			var runnerPort = runnerEngine.Start();
			var executionEngine = new TcpExecutionEngine("1e", runnerPort, emptyFilters, nullMessageSink);
			var executionPort = await executionEngine.Start();
			await WaitForConnection(runnerEngine);

			await executionEngine.DisposeAsync();  // Shut down before asked to, simulates a crash
			await runnerEngine.DisposeAsync();

			Assert.Collection(
				messages.OfType<_DiagnosticMessage>().Select(dm => dm.Message),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Listening on tcp://localhost:{runnerPort}/", msg),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Connection accepted from tcp://localhost:{executionPort}/", msg),
				msg => Assert.StartsWith("BufferTcpClient(runner::1r): abnormal termination of pipe", msg),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Disconnecting from tcp://localhost:{executionPort}/", msg),
				msg => Assert.Equal($"TcpRunnerEngine(1r): Disconnected from tcp://localhost:{executionPort}/", msg)
			);
		}

		[Fact]
		public async ValueTask ExecutionEngine_WithConnection()
		{
			var runnerEngine = new TcpRunnerEngine("1r", nullMessageSink, nullMessageSink);
			var runnerPort = runnerEngine.Start();
			var executionEngine = new TcpExecutionEngine("1e", runnerPort, emptyFilters, spyMessageSink);
			await executionEngine.Start();
			await WaitForConnection(runnerEngine);

			await runnerEngine.DisposeAsync();
			await WaitForQuit(executionEngine);
			await executionEngine.DisposeAsync();

			Assert.Collection(
				messages.OfType<_DiagnosticMessage>().Select(dm => dm.Message),
				msg => Assert.Equal($"TcpExecutionEngine(1e): Connecting to tcp://localhost:{runnerPort}/", msg),
				msg => Assert.Equal($"TcpExecutionEngine(1e): Connected to tcp://localhost:{runnerPort}/", msg),
				msg => Assert.Equal($"TcpExecutionEngine(1e): Disconnecting from tcp://localhost:{runnerPort}/", msg),
				msg => Assert.Equal($"TcpExecutionEngine(1e): Disconnected from tcp://localhost:{runnerPort}/", msg)
			);
		}
	}

	public class Quit
	{
		readonly XunitFilters emptyFilters = new();
		readonly _NullMessageSink nullMessageSink = new();

		[Fact]
		public async ValueTask WhenRunnerSendsQuit_ExecutionEngineStops()
		{
			await using var runnerEngine = new TcpRunnerEngine("1r", nullMessageSink, nullMessageSink);
			var runnerPort = runnerEngine.Start();
			await using var executionEngine = new TcpExecutionEngine("1e", runnerPort, emptyFilters, nullMessageSink);
			await executionEngine.Start();
			await WaitForConnection(runnerEngine);

			runnerEngine.SendQuit();
			var success = await WaitForQuit(executionEngine, throwOnFailure: false);

			Assert.True(success, "Timed out waiting for the QUIT signal to arrive");
		}

		[Fact]
		public async ValueTask DisposingRunnerBeforeSendingQuit_SendsQuit()
		{
			var runnerEngine = new TcpRunnerEngine("1r", nullMessageSink, nullMessageSink);
			var runnerPort = runnerEngine.Start();
			await using var executionEngine = new TcpExecutionEngine("1e", runnerPort, emptyFilters, nullMessageSink);
			await executionEngine.Start();
			await WaitForConnection(runnerEngine);

			await runnerEngine.DisposeAsync();
			var success = await WaitForQuit(executionEngine, throwOnFailure: false);

			Assert.True(success, "Timed out waiting for the QUIT signal to arrive");
		}
	}

	static Task<bool> WaitForConnection(
		TcpRunnerEngine runnerEngine,
		bool throwOnFailure = true,
		TimeSpan? timeout = null) =>
			Task.Run(async () =>
			{
				var end = DateTimeOffset.Now.Add(timeout ?? TimeSpan.FromSeconds(5));

				while (true)
				{
					if (runnerEngine.Connected)
						return true;

					if (DateTimeOffset.Now > end)
					{
						if (throwOnFailure)
							throw new InvalidOperationException("Timed out waiting for execution engine connection");
						return false;
					}

					await Task.Delay(50);
				}
			});

	static Task<bool> WaitForQuit(
		TcpExecutionEngine executionEngine,
		bool throwOnFailure = true,
		TimeSpan? timeout = null) =>
			Task.Run(async () =>
			{
				var timerTask = Task.Delay(timeout ?? TimeSpan.FromSeconds(5));
				var waitForQuitTask = executionEngine.WaitForQuit();
				var finishedTask = await Task.WhenAny(waitForQuitTask, timerTask);

				if (throwOnFailure && finishedTask != waitForQuitTask)
					throw new InvalidOperationException("Timed out waiting for the QUIT signal to arrive");

				return finishedTask == waitForQuitTask;
			});
}
