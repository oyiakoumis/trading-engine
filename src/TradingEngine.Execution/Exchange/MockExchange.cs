using System.Collections.Concurrent;
using TradingEngine.Domain.Entities;
using TradingEngine.Domain.Enums;
using TradingEngine.Domain.ValueObjects;
using TradingEngine.Execution.Interfaces;
using TradingEngine.Execution.Services;

namespace TradingEngine.Execution.Exchange
{
    /// <summary>
    /// Simulated exchange for order matching and execution
    /// Provides realistic fill simulation with latency and slippage
    /// </summary>
    public class MockExchange : IExchange
    {
        private readonly ConcurrentDictionary<Symbol, OrderBook> _orderBooks;
        private readonly ConcurrentDictionary<OrderId, Order> _pendingOrders;
        private readonly ConcurrentQueue<ExecutionReport> _executionReports;
        private readonly OrderManager _orderManager;
        private readonly Random _random = new();
        private readonly Timer _matchingEngine;
        private readonly SemaphoreSlim _executionSemaphore;
        private bool _isRunning;
        private bool _disposed;

        // Configurable parameters
        public int SimulatedLatencyMs { get; set; } = 10;
        public decimal SlippagePercent { get; set; } = 0.01m;
        public decimal PartialFillProbability { get; set; } = 0.2m;
        public decimal RejectProbability { get; set; } = 0.05m;

        public event EventHandler<ExecutionReport>? ExecutionReported;
        public event EventHandler<string>? ExchangeError;

        public MockExchange(OrderManager orderManager)
        {
            _orderManager = orderManager;
            _orderBooks = new ConcurrentDictionary<Symbol, OrderBook>();
            _pendingOrders = new ConcurrentDictionary<OrderId, Order>();
            _executionReports = new ConcurrentQueue<ExecutionReport>();
            _executionSemaphore = new SemaphoreSlim(1, 1);

            // Run matching engine every 100ms
            _matchingEngine = new Timer(
                async _ => await ProcessOrdersAsync(),
                null,
                Timeout.Infinite,
                Timeout.Infinite
            );
        }

        /// <summary>
        /// Start the exchange
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;

            _isRunning = true;
            _matchingEngine.Change(TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        }

        /// <summary>
        /// Stop the exchange
        /// </summary>
        public void Stop()
        {
            _isRunning = false;
            _matchingEngine.Change(Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// Submit order to exchange
        /// </summary>
        public async Task<bool> SubmitOrderAsync(Order order)
        {
            try
            {
                // Simulate network latency
                await Task.Delay(SimulatedLatencyMs);

                // Random rejection
                if (_random.NextDouble() < (double)RejectProbability)
                {
                    var reason = GetRandomRejectionReason();
                    _orderManager.RejectOrder(order.Id, reason);

                    var report = new ExecutionReport
                    {
                        OrderId = order.Id,
                        Symbol = order.Symbol,
                        Status = OrderStatus.Rejected,
                        Reason = reason,
                        Timestamp = Timestamp.Now
                    };

                    _executionReports.Enqueue(report);
                    ExecutionReported?.Invoke(this, report);
                    return false;
                }

                // Accept order
                _pendingOrders.TryAdd(order.Id, order);
                _orderManager.SubmitOrder(order.Id);

                // Add to order book
                var orderBook = _orderBooks.GetOrAdd(order.Symbol, _ => new OrderBook());
                await orderBook.AddOrderAsync(order);

                _orderManager.AcceptOrder(order.Id);

                var acceptReport = new ExecutionReport
                {
                    OrderId = order.Id,
                    Symbol = order.Symbol,
                    Status = OrderStatus.Accepted,
                    Timestamp = Timestamp.Now
                };

                _executionReports.Enqueue(acceptReport);
                ExecutionReported?.Invoke(this, acceptReport);

                return true;
            }
            catch (Exception ex)
            {
                ExchangeError?.Invoke(this, $"Error submitting order {order.Id}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Cancel order on exchange
        /// </summary>
        public async Task<bool> CancelOrderAsync(OrderId orderId)
        {
            await Task.Delay(SimulatedLatencyMs);

            if (_pendingOrders.TryRemove(orderId, out var order))
            {
                if (_orderBooks.TryGetValue(order.Symbol, out var orderBook))
                {
                    await orderBook.RemoveOrderAsync(orderId);
                }

                var report = new ExecutionReport
                {
                    OrderId = orderId,
                    Symbol = order.Symbol,
                    Status = OrderStatus.Cancelled,
                    Reason = "Exchange confirmed cancellation",
                    Timestamp = Timestamp.Now
                };

                _executionReports.Enqueue(report);
                ExecutionReported?.Invoke(this, report);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Update market price for matching
        /// </summary>
        public void UpdateMarketPrice(Symbol symbol, Price bidPrice, Price askPrice)
        {
            var orderBook = _orderBooks.GetOrAdd(symbol, _ => new OrderBook());
            orderBook.UpdateMarketPrice(bidPrice, askPrice);
        }

        /// <summary>
        /// Process orders for matching
        /// </summary>
        private async Task ProcessOrdersAsync()
        {
            if (!_isRunning) return;

            await _executionSemaphore.WaitAsync();
            try
            {
                foreach (var kvp in _orderBooks)
                {
                    var symbol = kvp.Key;
                    var orderBook = kvp.Value;

                    // Get orders ready for execution
                    var executableOrders = await orderBook.GetExecutableOrdersAsync();

                    foreach (var order in executableOrders)
                    {
                        await ExecuteOrderAsync(order, orderBook);
                    }
                }
            }
            catch (Exception ex)
            {
                ExchangeError?.Invoke(this, $"Matching engine error: {ex.Message}");
            }
            finally
            {
                _executionSemaphore.Release();
            }
        }

        /// <summary>
        /// Execute an order
        /// </summary>
        private async Task ExecuteOrderAsync(Order order, OrderBook orderBook)
        {
            try
            {
                // Simulate execution latency
                await Task.Delay(_random.Next(5, SimulatedLatencyMs));

                // Calculate fill details
                var fillPrice = CalculateFillPrice(order, orderBook);
                var fillQuantity = CalculateFillQuantity(order);

                // Create trade
                var trade = new Trade(
                    order.Id,
                    order.Symbol,
                    order.Side,
                    fillPrice,
                    fillQuantity,
                    CalculateCommission(fillQuantity, fillPrice),
                    "MockExchange"
                );

                // Update order
                _orderManager.FillOrder(order.Id, fillQuantity, fillPrice, trade.Commission);

                // Check if fully filled
                if (order.Status == OrderStatus.Filled)
                {
                    _pendingOrders.TryRemove(order.Id, out _);
                    await orderBook.RemoveOrderAsync(order.Id);
                }

                // Generate execution report
                var report = new ExecutionReport
                {
                    OrderId = order.Id,
                    Symbol = order.Symbol,
                    Status = order.Status,
                    Trade = trade,
                    Timestamp = Timestamp.Now
                };

                _executionReports.Enqueue(report);
                ExecutionReported?.Invoke(this, report);
            }
            catch (Exception ex)
            {
                ExchangeError?.Invoke(this, $"Error executing order {order.Id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Calculate fill price with slippage
        /// </summary>
        private Price CalculateFillPrice(Order order, OrderBook orderBook)
        {
            var basePrice = order.Type == OrderType.Market
                ? (order.Side == OrderSide.Buy ? orderBook.BestAsk : orderBook.BestBid)
                : order.LimitPrice ?? orderBook.MidPrice;

            // Apply slippage
            var slippage = basePrice.Value * (SlippagePercent / 100) * (decimal)_random.NextDouble();

            if (order.Side == OrderSide.Buy)
            {
                // Buyers pay more (negative slippage)
                return new Price(basePrice.Value + slippage);
            }
            else
            {
                // Sellers receive less (negative slippage)
                return new Price(basePrice.Value - slippage);
            }
        }

        /// <summary>
        /// Calculate fill quantity (may be partial)
        /// </summary>
        private Quantity CalculateFillQuantity(Order order)
        {
            // Check for partial fill
            if (_random.NextDouble() < (double)PartialFillProbability && order.RemainingQuantity.Value > 100)
            {
                // Fill 20-80% of remaining quantity
                var fillPercent = (decimal)(0.2 + _random.NextDouble() * 0.6);
                var fillQty = order.RemainingQuantity.Value * fillPercent;
                return new Quantity(Math.Floor(fillQty));
            }

            // Full fill
            return order.RemainingQuantity;
        }

        /// <summary>
        /// Calculate commission
        /// </summary>
        private decimal CalculateCommission(Quantity quantity, Price price)
        {
            // Simple commission model: $0.005 per share or 0.1% of notional, whichever is higher
            var perShare = quantity.Value * 0.005m;
            var percentage = quantity.Value * price.Value * 0.001m;
            return Math.Max(perShare, percentage);
        }

        /// <summary>
        /// Get random rejection reason
        /// </summary>
        private string GetRandomRejectionReason()
        {
            var reasons = new[]
            {
                "Insufficient buying power",
                "Symbol not tradeable",
                "Market closed",
                "Order size exceeds limit",
                "Invalid price",
                "Risk check failed"
            };
            return reasons[_random.Next(reasons.Length)];
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            Stop();
            _matchingEngine?.Dispose();
            _executionSemaphore?.Dispose();
        }
    }

    /// <summary>
    /// Simple order book implementation
    /// </summary>
    internal class OrderBook
    {
        private readonly SortedDictionary<Price, Queue<Order>> _bids;
        private readonly SortedDictionary<Price, Queue<Order>> _asks;
        private readonly ConcurrentDictionary<OrderId, Order> _allOrders;
        private readonly SemaphoreSlim _bookSemaphore;

        public Price BestBid { get; private set; }
        public Price BestAsk { get; private set; }
        public Price MidPrice => new((BestBid.Value + BestAsk.Value) / 2);

        public OrderBook()
        {
            _bids = new SortedDictionary<Price, Queue<Order>>(new PriceComparerDescending());
            _asks = new SortedDictionary<Price, Queue<Order>>();
            _allOrders = new ConcurrentDictionary<OrderId, Order>();
            _bookSemaphore = new SemaphoreSlim(1, 1);

            // Initialize with default spread
            BestBid = new Price(100);
            BestAsk = new Price(100.02m);
        }

        public void UpdateMarketPrice(Price bid, Price ask)
        {
            BestBid = bid;
            BestAsk = ask;
        }

        public async Task AddOrderAsync(Order order)
        {
            await _bookSemaphore.WaitAsync();
            try
            {
                _allOrders.TryAdd(order.Id, order);

                if (order.Type == OrderType.Limit && order.LimitPrice.HasValue)
                {
                    var book = order.Side == OrderSide.Buy ? _bids : _asks;
                    var price = order.LimitPrice.Value;

                    if (!book.ContainsKey(price))
                    {
                        book[price] = new Queue<Order>();
                    }

                    book[price].Enqueue(order);
                }
            }
            finally
            {
                _bookSemaphore.Release();
            }
        }

        public async Task RemoveOrderAsync(OrderId orderId)
        {
            await _bookSemaphore.WaitAsync();
            try
            {
                _allOrders.TryRemove(orderId, out _);
            }
            finally
            {
                _bookSemaphore.Release();
            }
        }

        public async Task<List<Order>> GetExecutableOrdersAsync()
        {
            await _bookSemaphore.WaitAsync();
            try
            {
                var executableOrders = new List<Order>();

                // Check market orders first (always executable)
                foreach (var order in _allOrders.Values.Where(o => o.Type == OrderType.Market && o.IsActive))
                {
                    executableOrders.Add(order);
                }

                // Check limit orders
                foreach (var order in _allOrders.Values.Where(o => o.Type == OrderType.Limit && o.IsActive))
                {
                    if (order.LimitPrice.HasValue)
                    {
                        var isExecutable = order.Side == OrderSide.Buy
                            ? order.LimitPrice.Value >= BestAsk
                            : order.LimitPrice.Value <= BestBid;

                        if (isExecutable)
                        {
                            executableOrders.Add(order);
                        }
                    }
                }

                return executableOrders;
            }
            finally
            {
                _bookSemaphore.Release();
            }
        }

        private class PriceComparerDescending : IComparer<Price>
        {
            public int Compare(Price x, Price y) => y.CompareTo(x);
        }
    }

    /// <summary>
    /// Execution report
    /// </summary>
    public class ExecutionReport
    {
        public OrderId OrderId { get; set; }
        public Symbol Symbol { get; set; }
        public OrderStatus Status { get; set; }
        public Trade? Trade { get; set; }
        public string? Reason { get; set; }
        public Timestamp Timestamp { get; set; }

        public override string ToString()
        {
            if (Trade != null)
            {
                return $"Execution: {OrderId.ToShortString()} | {Status} | " +
                       $"Filled {Trade.ExecutionQuantity} @ {Trade.ExecutionPrice}";
            }
            return $"Execution: {OrderId.ToShortString()} | {Status} | {Reason}";
        }
    }
}