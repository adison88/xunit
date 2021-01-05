﻿using System;

namespace Xunit.v3
{
	/// <summary>
	/// Byte sequences which represent commands issued between the runner and execution engines.
	/// </summary>
	public static class TcpEngineMessages
	{
		/// <summary>
		/// Delineates the end of a message from one engine to the other. Guaranteed to always
		/// be a single byte.
		/// </summary>
		public static readonly byte[] EndOfMessage = new[] { (byte)'\n' };

		/// <summary>
		/// Delineates the separator between command and data, or elements of data. Guaranteed to
		/// always be a single byte.
		/// </summary>
		public static readonly byte[] Separator = new[] { (byte)' ' };

		/// <summary>
		/// Splits an instance of <see cref="ReadOnlyMemory{T}"/> looking for a separator.
		/// </summary>
		/// <param name="memory">The memory to split</param>
		/// <returns>The value to the left of the separator, and the rest. If the separator
		/// was not found, then value will contain the entire memory block, and rest will
		/// be <c>null</c>.</returns>
		public static (ReadOnlyMemory<byte> value, ReadOnlyMemory<byte>? rest) SplitOnSeparator(ReadOnlyMemory<byte> memory)
		{
			var separatorIndex = memory.Span.IndexOf(Separator[0]);

			if (separatorIndex < 0)
				return (memory, null);
			else
				return (memory.Slice(0, separatorIndex), memory.Slice(separatorIndex + 1));
		}

		/// <summary>
		/// Byte sequences which represent commands issued from the execution engine.
		/// </summary>
		public static class Execution
		{
			/// <summary>
			/// Sent to indicate that this is a message intended as a result of an operation (typically a find or run operation).
			/// The data for the cancelation is the operation ID, followed by <see cref="Separator"/>, followed by the
			/// JSON-encoded message data.
			/// </summary>
			public static readonly byte[] Message = new[] { (byte)'M', (byte)'S', (byte)'G' };
		}

		/// <summary>
		/// Byte sequences which represent commands issued from the runner engine.
		/// </summary>
		public static class Runner
		{
			/// <summary>
			/// Sent to indicate that a test run should be canceled. The data is the operation ID.
			/// </summary>
			public static readonly byte[] Cancel = new[] { (byte)'C', (byte)'A', (byte)'N', (byte)'C', (byte)'E', (byte)'L' };

			/// <summary>
			/// Send to indicate that the engine should gracefully shut down, after ensuring that all pending response messages
			/// are sent. There is no data for this message.
			/// </summary>
			public static readonly byte[] Quit = new[] { (byte)'Q', (byte)'U', (byte)'I', (byte)'T' };
		}
	}
}
