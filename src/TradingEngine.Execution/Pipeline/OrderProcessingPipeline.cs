using TradingEngine.Execution.Pipeline.Interfaces;
using TradingEngine.Execution.Pipeline.Models;
using TradingEngine.Strategies.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace TradingEngine.Execution.Pipeline
{
    /// <summary>
    /// High-performance order processing pipeline implementation
    /// Chain of responsibility pattern with pluggable stages
    /// Thread-safe with concurrent processing support
    /// </summary>
    public sealed class OrderProcessingPipeline : IOrderProcessingPipeline
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<OrderProcessingPipeline>? _logger;
        private readonly SemaphoreSlim _pipelineSemaphore;
        private readonly OrderPipelineMetrics _metrics;
        
        private readonly ConcurrentDictionary<Type, IOrderProcessingStage> _stages;
        private readonly List<IOrderProcessingStage> _orderedStages;
        private readonly object _stagesLock = new();
        
        private volatile bool _isRunning;
        private bool _disposed;

        public event EventHandler<OrderProcessingCompletedEventArgs>? ProcessingCompleted;
        public event EventHandler<OrderProcessingErrorEventArgs>? ProcessingError;

        public OrderProcessingPipeline(
            IServiceProvider serviceProvider,
            ILogger<OrderProcessingPipeline>? logger = null)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _logger = logger;
            
            _pipelineSemaphore = new SemaphoreSlim(Environment.ProcessorCount * 2, Environment.ProcessorCount * 2);
            _metrics = new OrderPipelineMetrics();
            _stages = new ConcurrentDictionary<Type, IOrderProcessingStage>();
            _orderedStages = new List<IOrderProcessingStage>();

            // Initialize with default stages
            InitializeDefaultStages();
        }

        private void InitializeDefaultStages()
        {
            try
            {
                // Add default stages in priority order
                AddStage<Stages.ValidationStage>();
                AddStage<Stages.RiskAssessmentStage>();
                AddStage<Stages.CreationStage>();
                AddStage<Stages.ExecutionStage>();
                AddStage<Stages.PostProcessingStage>();

                _logger?.LogInformation("Order processing pipeline initialized with {StageCount} stages", _orderedStages.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize default pipeline stages");
                throw;
            }
        }

        public async ValueTask<OrderProcessingResult> ProcessSignalAsync(
            Signal signal,
            OrderProcessingContext context,
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OrderProcessingPipeline));

            if (!_isRunning)
                throw new InvalidOperationException("Pipeline is not running. Call StartAsync first.");

            if (signal == null)
                throw new ArgumentNullException(nameof(signal));

            var processingStartTime = DateTime.UtcNow;
            var workingContext = context ?? new OrderProcessingContext(signal);
            var stageResults = new List<(string StageName, StageResult Result, TimeSpan Duration)>();

            // Acquire processing semaphore to limit concurrent processing
            await _pipelineSemaphore.WaitAsync(cancellationToken);

            try
            {
                _logger?.LogDebug(
                    "Starting order processing for signal {CorrelationId}: {Symbol} {Side} {Quantity}",
                    workingContext.CorrelationId,
                    signal.Symbol,
                    signal.Side,
                    signal.Quantity);

                // Process through each stage in order
                foreach (var stage in _orderedStages)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var stageStartTime = DateTime.UtcNow;
                    
                    try
                    {
                        // Check if stage can process this context
                        if (!await stage.CanProcessAsync(workingContext))
                        {
                            _logger?.LogWarning(
                                "Stage {StageName} cannot process signal {CorrelationId}, skipping",
                                stage.StageName,
                                workingContext.CorrelationId);
                            continue;
                        }

                        // Execute stage processing
                        var stageResult = await stage.ProcessAsync(workingContext, cancellationToken);
                        var stageDuration = DateTime.UtcNow - stageStartTime;

                        stageResults.Add((stage.StageName, stageResult, stageDuration));

                        _logger?.LogDebug(
                            "Stage {StageName} completed for signal {CorrelationId}: {Success} in {Duration}ms",
                            stage.StageName,
                            workingContext.CorrelationId,
                            stageResult.IsSuccess,
                            stageDuration.TotalMilliseconds);

                        // Handle stage failure
                        if (!stageResult.IsSuccess)
                        {
                            var errorMessage = $"Stage '{stage.StageName}' failed: {stageResult.ErrorMessage}";
                            workingContext = workingContext.AddError(errorMessage);

                            _logger?.LogWarning(
                                "Pipeline stage {StageName} failed for signal {CorrelationId}: {ErrorMessage}",
                                stage.StageName,
                                workingContext.CorrelationId,
                                stageResult.ErrorMessage);

                            // Stop pipeline processing on stage failure
                            break;
                        }

                        // Update context with stage results
                        if (stageResult.Data.Count > 0)
                        {
                            foreach (var kvp in stageResult.Data)
                            {
                                workingContext = workingContext.SetProperty(kvp.Key, kvp.Value);
                            }
                        }

                        // Special handling for order ID from creation stage
                        if (stage is Stages.CreationStage && stageResult.Data.TryGetValue("OrderId", out var orderIdObj))
                        {
                            if (orderIdObj is Domain.ValueObjects.OrderId orderId)
                            {
                                workingContext = workingContext.SetOrderId(orderId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var stageDuration = DateTime.UtcNow - stageStartTime;
                        var errorMessage = $"Stage '{stage.StageName}' threw exception: {ex.Message}";
                        
                        workingContext = workingContext.AddError(errorMessage);
                        stageResults.Add((stage.StageName, StageResult.Failed(errorMessage, stageDuration), stageDuration));

                        _logger?.LogError(ex,
                            "Unhandled exception in stage {StageName} for signal {CorrelationId}",
                            stage.StageName,
                            workingContext.CorrelationId);

                        // Raise error event
                        RaiseProcessingError(signal, ex, stage.StageName, workingContext.CorrelationId);

                        // Stop pipeline processing on unhandled exception
                        break;
                    }
                }

                // Calculate final processing time
                var totalProcessingTime = DateTime.UtcNow - processingStartTime;

                // Create final result
                var result = workingContext.HasErrors
                    ? OrderProcessingResult.Failure(
                        workingContext.Errors.ToList(),
                        totalProcessingTime,
                        workingContext.CorrelationId,
                        CreateResultMetadata(stageResults))
                    : OrderProcessingResult.Success(
                        workingContext.OrderId!.Value,
                        totalProcessingTime,
                        workingContext.RiskLevel,
                        workingContext.CorrelationId,
                        CreateResultMetadata(stageResults));

                // Update metrics
                if (result.IsSuccess)
                {
                    _metrics.RecordSuccess(totalProcessingTime);
                }
                else
                {
                    _metrics.RecordFailure(totalProcessingTime);
                }

                // Raise completion event
                RaiseProcessingCompleted(signal, result, workingContext.CorrelationId);

                _logger?.LogInformation(
                    "Order processing {Status} for signal {CorrelationId} in {Duration}ms: {OrderId}",
                    result.IsSuccess ? "completed" : "failed",
                    workingContext.CorrelationId,
                    totalProcessingTime.TotalMilliseconds,
                    result.OrderId?.ToString() ?? "N/A");

                return result;
            }
            finally
            {
                _pipelineSemaphore.Release();
            }
        }

        private Dictionary<string, object> CreateResultMetadata(
            List<(string StageName, StageResult Result, TimeSpan Duration)> stageResults)
        {
            var metadata = new Dictionary<string, object>();

            metadata["StageCount"] = stageResults.Count;
            metadata["StageResults"] = stageResults.Select(sr => new
            {
                sr.StageName,
                Success = sr.Result.IsSuccess,
                DurationMs = sr.Duration.TotalMilliseconds,
                Error = sr.Result.ErrorMessage
            }).ToList();

            var totalStageTime = TimeSpan.FromMilliseconds(stageResults.Sum(sr => sr.Duration.TotalMilliseconds));
            metadata["TotalStageProcessingTime"] = totalStageTime;

            return metadata;
        }

        public IOrderProcessingPipeline AddStage<TStage>() where TStage : class, IOrderProcessingStage
        {
            lock (_stagesLock)
            {
                if (_stages.ContainsKey(typeof(TStage)))
                {
                    _logger?.LogWarning("Stage {StageType} already exists in pipeline", typeof(TStage).Name);
                    return this;
                }

                try
                {
                    var stage = _serviceProvider.GetRequiredService<TStage>();
                    _stages.TryAdd(typeof(TStage), stage);
                    
                    // Rebuild ordered stages list
                    _orderedStages.Clear();
                    _orderedStages.AddRange(_stages.Values.OrderBy(s => s.Priority));

                    _logger?.LogDebug("Added stage {StageName} with priority {Priority}", stage.StageName, stage.Priority);
                    return this;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to add stage {StageType}", typeof(TStage).Name);
                    throw;
                }
            }
        }

        public IOrderProcessingPipeline RemoveStage<TStage>() where TStage : class, IOrderProcessingStage
        {
            lock (_stagesLock)
            {
                if (_stages.TryRemove(typeof(TStage), out var removedStage))
                {
                    // Rebuild ordered stages list
                    _orderedStages.Clear();
                    _orderedStages.AddRange(_stages.Values.OrderBy(s => s.Priority));

                    _logger?.LogDebug("Removed stage {StageName}", removedStage.StageName);
                }

                return this;
            }
        }

        public OrderPipelineMetrics GetMetrics() => _metrics;

        public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                return HealthCheckResult.Unhealthy("Pipeline is disposed");

            try
            {
                var startTime = DateTime.UtcNow;
                var healthData = new Dictionary<string, object>();
                var stageHealthResults = new List<object>();
                var allStagesHealthy = true;

                // Check each stage health
                foreach (var stage in _orderedStages)
                {
                    try
                    {
                        var stageHealth = await stage.CheckHealthAsync(cancellationToken);
                        stageHealthResults.Add(new
                        {
                            StageName = stage.StageName,
                            IsHealthy = stageHealth.IsHealthy,
                            Description = stageHealth.Description,
                            ResponseTime = stageHealth.ResponseTime.TotalMilliseconds
                        });

                        if (!stageHealth.IsHealthy)
                            allStagesHealthy = false;
                    }
                    catch (Exception ex)
                    {
                        stageHealthResults.Add(new
                        {
                            StageName = stage.StageName,
                            IsHealthy = false,
                            Description = $"Health check failed: {ex.Message}",
                            ResponseTime = 0.0
                        });
                        allStagesHealthy = false;
                    }
                }

                var responseTime = DateTime.UtcNow - startTime;
                var metrics = GetMetrics();

                healthData.Add("IsRunning", _isRunning);
                healthData.Add("StageCount", _orderedStages.Count);
                healthData.Add("StageHealth", stageHealthResults);
                healthData.Add("Metrics", new
                {
                    metrics.TotalProcessed,
                    metrics.TotalSuccessful,
                    metrics.TotalFailed,
                    SuccessRate = metrics.SuccessRate,
                    AverageProcessingTime = metrics.AverageProcessingTimeMs
                });

                if (!_isRunning || !allStagesHealthy)
                {
                    var issues = new List<string>();
                    if (!_isRunning) issues.Add("Pipeline not running");
                    if (!allStagesHealthy) issues.Add("One or more stages unhealthy");

                    return HealthCheckResult.Unhealthy(
                        $"Pipeline issues: {string.Join(", ", issues)}",
                        responseTime,
                        healthData);
                }

                return HealthCheckResult.Healthy(
                    "Order processing pipeline is healthy",
                    responseTime,
                    healthData);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Pipeline health check failed");
                return HealthCheckResult.Unhealthy($"Health check failed: {ex.Message}");
            }
        }

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OrderProcessingPipeline));

            if (_isRunning)
            {
                _logger?.LogWarning("Pipeline is already running");
                return Task.CompletedTask;
            }

            try
            {
                _isRunning = true;
                _logger?.LogInformation("Order processing pipeline started with {StageCount} stages", _orderedStages.Count);
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _isRunning = false;
                _logger?.LogError(ex, "Failed to start order processing pipeline");
                throw;
            }
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            if (!_isRunning)
            {
                _logger?.LogWarning("Pipeline is not running");
                return Task.CompletedTask;
            }

            try
            {
                _isRunning = false;
                _logger?.LogInformation("Order processing pipeline stopped");
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error stopping order processing pipeline");
                throw;
            }
        }

        private void RaiseProcessingCompleted(Signal signal, OrderProcessingResult result, string? correlationId)
        {
            try
            {
                var args = new OrderProcessingCompletedEventArgs(signal, result, correlationId);
                ProcessingCompleted?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error raising ProcessingCompleted event");
            }
        }

        private void RaiseProcessingError(Signal signal, Exception exception, string stageName, string? correlationId)
        {
            try
            {
                var args = new OrderProcessingErrorEventArgs(signal, exception, stageName, correlationId);
                ProcessingError?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error raising ProcessingError event");
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            try
            {
                if (_isRunning)
                {
                    StopAsync().GetAwaiter().GetResult();
                }

                _pipelineSemaphore?.Dispose();

                // Dispose stages if they implement IDisposable
                foreach (var stage in _stages.Values.OfType<IDisposable>())
                {
                    try
                    {
                        stage.Dispose();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error disposing stage {StageName}", stage.GetType().Name);
                    }
                }

                _stages.Clear();
                _orderedStages.Clear();

                _logger?.LogInformation("Order processing pipeline disposed");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error during pipeline disposal");
            }
        }
    }
}