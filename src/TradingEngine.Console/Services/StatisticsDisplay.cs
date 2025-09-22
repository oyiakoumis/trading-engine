using TradingEngine.Infrastructure.Pipeline;

namespace TradingEngine.Console.Display
{
    internal class StatisticsDisplay
    {
        private const int StatisticsLineCount = 5;

        public static void DisplayHeader()
        {
            global::System.Console.WriteLine("===========================================");
            global::System.Console.WriteLine("     TICK-TO-TRADE PIPELINE DEMO");
            global::System.Console.WriteLine("===========================================");
            global::System.Console.WriteLine();
        }

        public static void DisplayStartupMessage(string[] symbols)
        {
            global::System.Console.WriteLine("Starting Trading Pipeline...");
            global::System.Console.WriteLine($"Trading symbols: {string.Join(", ", symbols)}");
            global::System.Console.WriteLine();
        }

        public static void DisplayRunningMessage()
        {
            global::System.Console.WriteLine("\nTrading system is running. Press Ctrl+C to stop.\n");
            global::System.Console.WriteLine("Live Statistics:");
            global::System.Console.WriteLine("================");
        }

        public static async Task DisplayLiveStatisticsLoop(TradingPipeline pipeline, CancellationToken cancellationToken, int updateIntervalMs)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(updateIntervalMs, cancellationToken);

                    if (pipeline != null)
                    {
                        var stats = pipeline.GetStatistics();
                        DisplayLiveStatistics(stats);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private static void DisplayLiveStatistics(dynamic stats)
        {
            // Try to reposition cursor for live updates
            try
            {
                int currentTop = global::System.Console.CursorTop;
                int targetTop = Math.Max(0, currentTop - StatisticsLineCount - 1);

                if (targetTop >= 0 && targetTop < global::System.Console.BufferHeight)
                {
                    global::System.Console.SetCursorPosition(0, targetTop);
                    global::System.Console.WriteLine($"Uptime:     {stats.Uptime:hh\\:mm\\:ss}                    ");
                    global::System.Console.WriteLine($"Ticks:      {stats.TicksProcessed:N0}                  ");
                    global::System.Console.WriteLine($"Signals:    {stats.SignalsGenerated:N0}                ");
                    global::System.Console.WriteLine($"Orders:     {stats.OrdersExecuted:N0}                  ");
                    global::System.Console.WriteLine($"Positions:  {stats.ActivePositions}                    ");
                    global::System.Console.WriteLine();
                    return;
                }
            }
            catch
            {
                // Fall back to single line display if cursor manipulation fails
            }

            // Fallback: single line display
            global::System.Console.WriteLine($"\rUptime: {stats.Uptime:hh\\:mm\\:ss} | Ticks: {stats.TicksProcessed:N0} | Signals: {stats.SignalsGenerated:N0} | Orders: {stats.OrdersExecuted:N0} | Positions: {stats.ActivePositions}");
        }

        public static void DisplayFinalStatistics(TradingPipeline pipeline)
        {
            if (pipeline == null) return;

            var stats = pipeline.GetStatistics();

            global::System.Console.WriteLine("\n===========================================");
            global::System.Console.WriteLine("          FINAL STATISTICS");
            global::System.Console.WriteLine("===========================================");
            global::System.Console.WriteLine($"Total Runtime:        {stats.Uptime:hh\\:mm\\:ss}");
            global::System.Console.WriteLine($"Ticks Processed:      {stats.TicksProcessed:N0}");
            global::System.Console.WriteLine($"Signals Generated:    {stats.SignalsGenerated:N0}");
            global::System.Console.WriteLine($"Orders Executed:      {stats.OrdersExecuted:N0}");
            global::System.Console.WriteLine($"Active Positions:     {stats.ActivePositions}");

            if (stats.EventBusStats != null)
            {
                global::System.Console.WriteLine($"Events Published:     {stats.EventBusStats.TotalEventsPublished:N0}");
                global::System.Console.WriteLine($"Events Processed:     {stats.EventBusStats.TotalEventsProcessed:N0}");
                global::System.Console.WriteLine($"Avg Processing Time:  {stats.EventBusStats.AverageProcessingTime.TotalMilliseconds:F2}ms");
            }

            global::System.Console.WriteLine("===========================================");
        }

        public static void WriteColoredLine(string message, ConsoleColor color)
        {
            var originalColor = global::System.Console.ForegroundColor;
            global::System.Console.ForegroundColor = color;
            global::System.Console.WriteLine(message);
            global::System.Console.ForegroundColor = originalColor;
        }

        public static void DisplayShutdownMessage()
        {
            global::System.Console.WriteLine("\nShutting down...");
        }

        public static void DisplayCompletionMessage()
        {
            global::System.Console.WriteLine("\nTrade Engine demonstration completed.");
        }

        public static void DisplayShutdownSignal()
        {
            global::System.Console.WriteLine("\nShutdown signal received...");
        }

        public static void DisplayFatalError(Exception ex)
        {
            global::System.Console.WriteLine($"Fatal error: {ex.Message}");
            global::System.Console.WriteLine(ex.StackTrace);
        }
    }
}