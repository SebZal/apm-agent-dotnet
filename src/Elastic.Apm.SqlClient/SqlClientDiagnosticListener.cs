// Licensed to Elasticsearch B.V under one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using Elastic.Apm.Api;
using Elastic.Apm.DiagnosticSource;
using Elastic.Apm.Helpers;
using Elastic.Apm.Logging;
using Elastic.Apm.Model;

namespace Elastic.Apm.SqlClient
{
	internal class SqlClientDiagnosticListener : IDiagnosticListener
	{
		private readonly ApmAgent _apmAgent;
		private readonly IApmLogger _logger;
		private readonly PropertyFetcherSet _microsoftPropertyFetcherSet = new PropertyFetcherSet();

		private readonly ConcurrentDictionary<Guid, ISpan> _spans = new ConcurrentDictionary<Guid, ISpan>();

		private readonly PropertyFetcherSet _systemPropertyFetcherSet = new PropertyFetcherSet();

		public SqlClientDiagnosticListener(IApmAgent apmAgent)
		{
			_apmAgent = (ApmAgent)apmAgent;
			_logger = _apmAgent.Logger.Scoped(nameof(SqlClientDiagnosticListener));
		}

		public string Name => "SqlClientDiagnosticListener";

		// prefix - Microsoft.Data.SqlClient. or System.Data.SqlClient.
		public void OnNext(KeyValuePair<string, object> value)
		{
			// check for competing instrumentation
			if (_apmAgent.TracerInternal.CurrentSpan is Span span)
			{
				if (span.InstrumentationFlag == InstrumentationFlag.EfCore || span.InstrumentationFlag == InstrumentationFlag.EfClassic)
					return;
			}

			if (!value.Key.StartsWith("Microsoft.Data.SqlClient.") && !value.Key.StartsWith("System.Data.SqlClient.")) return;

			switch (value.Key)
			{
				case { } s when s.EndsWith("WriteCommandBefore") && _apmAgent.Tracer.CurrentTransaction != null:
					HandleStartCommand(value.Value, value.Key.StartsWith("System") ? _systemPropertyFetcherSet : _microsoftPropertyFetcherSet);
					break;
				case { } s when s.EndsWith("WriteCommandAfter"):
					HandleStopCommand(value.Value, value.Key.StartsWith("System") ? _systemPropertyFetcherSet : _microsoftPropertyFetcherSet);
					break;
				case { } s when s.EndsWith("WriteCommandError"):
					HandleErrorCommand(value.Value, value.Key.StartsWith("System") ? _systemPropertyFetcherSet : _microsoftPropertyFetcherSet);
					break;
			}
		}

		private void HandleStartCommand(object payloadData, PropertyFetcherSet propertyFetcherSet)
		{
			try
			{
				if (propertyFetcherSet.StartCorrelationId.Fetch(payloadData) is Guid operationId
					&& propertyFetcherSet.StartCommand.Fetch(payloadData) is IDbCommand dbCommand)
				{
					var span = _apmAgent.TracerInternal.DbSpanCommon.StartSpan(_apmAgent, dbCommand, InstrumentationFlag.SqlClient,
						ApiConstants.SubtypeMssql);
					_spans.TryAdd(operationId, span);
				}
			}
			catch (Exception ex)
			{
				//ignore
				_logger.Error()?.LogException(ex, "Exception was thrown while handling 'command started event'");
			}
		}

		private void HandleStopCommand(object payloadData, PropertyFetcherSet propertyFetcherSet)
		{
			try
			{
				if (propertyFetcherSet.StopCorrelationId.Fetch(payloadData) is Guid operationId
					&& propertyFetcherSet.StopCommand.Fetch(payloadData) is IDbCommand dbCommand)
				{
					if (!_spans.TryRemove(operationId, out var span)) return;

					TimeSpan? duration = null;

					if (propertyFetcherSet.Statistics.Fetch(payloadData) is IDictionary<object, object> statistics &&
						statistics.ContainsKey("ExecutionTime") && statistics["ExecutionTime"] is long durationInMs && durationInMs > 0)
						duration = TimeSpan.FromMilliseconds(durationInMs);

					_apmAgent.TracerInternal.DbSpanCommon.EndSpan(span, dbCommand, Outcome.Success, duration);
				}
			}
			catch (Exception ex)
			{
				// ignore
				_logger.Error()?.LogException(ex, "Exception was thrown while handling 'command succeeded event'");
			}
		}

		private void HandleErrorCommand(object payloadData, PropertyFetcherSet propertyFetcherSet)
		{
			try
			{
				if (propertyFetcherSet.ErrorCorrelationId.Fetch(payloadData) is Guid operationId)
				{
					if (!_spans.TryRemove(operationId, out var span)) return;

					if (propertyFetcherSet.Exception.Fetch(payloadData) is Exception exception) span.CaptureException(exception);

					if (propertyFetcherSet.ErrorCommand.Fetch(payloadData) is IDbCommand dbCommand)
						_apmAgent.TracerInternal.DbSpanCommon.EndSpan(span, dbCommand, Outcome.Failure);
					else
					{
						_logger.Warning()?.Log("Cannot extract database command from {PayloadData}", payloadData);
						span.Outcome = Outcome.Failure;
						span.End();
					}
				}
			}
			catch (Exception ex)
			{
				// ignore
				_logger.Error()?.LogException(ex, "Exception was thrown while handling 'command failed event'");
			}
		}

		public void OnError(Exception error)
		{
			// do nothing because it's not necessary to handle such event from provider
		}

		public void OnCompleted()
		{
			// do nothing because it's not necessary to handle such event from provider
		}

		private class PropertyFetcherSet
		{
			public PropertyFetcher ErrorCommand { get; } = new PropertyFetcher("Command");
			public PropertyFetcher ErrorCorrelationId { get; } = new PropertyFetcher("OperationId");

			public PropertyFetcher Exception { get; } = new PropertyFetcher("Exception");

			public PropertyFetcher StartCommand { get; } = new PropertyFetcher("Command");
			public PropertyFetcher StartCorrelationId { get; } = new PropertyFetcher("OperationId");

			public PropertyFetcher Statistics { get; } = new PropertyFetcher("Statistics");
			public PropertyFetcher StopCommand { get; } = new PropertyFetcher("Command");
			public PropertyFetcher StopCorrelationId { get; } = new PropertyFetcher("OperationId");
		}
	}
}
