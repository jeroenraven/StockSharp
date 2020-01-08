#region S# License
/******************************************************************************************
NOTICE!!!  This program and source code is owned and licensed by
StockSharp, LLC, www.stocksharp.com
Viewing or use of this code requires your acceptance of the license
agreement found at https://github.com/StockSharp/StockSharp/blob/master/LICENSE
Removal of this comment is a violation of the license agreement.

Project: StockSharp.Algo.Strategies.Algo
File: Strategy.cs
Created: 2015, 11, 11, 2:32 PM

Copyright 2010 by StockSharp, LLC
*******************************************************************************************/
#endregion S# License
namespace StockSharp.Algo.Strategies
{
	using System;
	using System.Collections.Generic;
	using System.ComponentModel;
	using System.ComponentModel.DataAnnotations;
	using System.Linq;

	using Ecng.Common;
	using Ecng.Collections;
	using Ecng.ComponentModel;
	using Ecng.Serialization;

	using MoreLinq;

	using StockSharp.Algo.Candles;
	using StockSharp.Algo.PnL;
	using StockSharp.Algo.Positions;
	using StockSharp.Algo.Risk;
	using StockSharp.Algo.Statistics;
	using StockSharp.Algo.Strategies.Messages;
	using StockSharp.BusinessEntities;
	using StockSharp.Logging;
	using StockSharp.Messages;
	using StockSharp.Localization;

	/// <summary>
	/// The base class for all trade strategies.
	/// </summary>
	public partial class Strategy : BaseLogReceiver, INotifyPropertyChangedEx, IMarketRuleContainer,
	    ICloneable<Strategy>, IMarketDataProviderEx, ISecurityProvider, ICandleManager,
	    ITransactionProvider
	{
		private class StrategyChangeStateMessage : Message
		{
			public Strategy Strategy { get; }
			public ProcessStates State { get; }

			public StrategyChangeStateMessage(Strategy strategy, ProcessStates state)
				: base(ExtendedMessageTypes.StrategyChangeState)
			{
				Strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
				State = state;
			}

			public override Message Clone()
			{
				return new StrategyChangeStateMessage(Strategy, State);
			}
		}

		private static readonly MemoryStatisticsValue<Strategy> _strategyStat = new MemoryStatisticsValue<Strategy>(LocalizedStrings.Str1355);

		static Strategy()
		{
			MemoryStatistics.Instance.Values.Add(_strategyStat);
		}

		private sealed class ChildStrategyList : SynchronizedSet<Strategy>, IStrategyChildStrategyList
		{
			private readonly Dictionary<Strategy, IMarketRule> _childStrategyRules = new Dictionary<Strategy, IMarketRule>();
			private readonly Strategy _parent;

			public ChildStrategyList(Strategy parent)
				: base(true)
			{
				_parent = parent ?? throw new ArgumentNullException(nameof(parent));
			}
			
			protected override void OnAdded(Strategy item)
			{
				//pyh: Нельзя использовать OnAdding тк логирование включается по событию Added которое вызовет base.OnAdded
				base.OnAdded(item);

				if (item.Parent != null)
					throw new ArgumentException(LocalizedStrings.Str1356);

				item.Parent = _parent;
				item.Connector = _parent.Connector;

				if (item.Portfolio == null)
					item.Portfolio = _parent.Portfolio;

				if (item.Security == null)
					item.Security = _parent.Security;

				item.OrderRegistering += _parent.ProcessChildOrderRegistering;
				item.OrderRegistered += _parent.ProcessOrder;
				//item.ReRegistering += _parent.ReRegisterSlippage;
				item.OrderChanged += _parent.OnChildOrderChanged;
				item.OrderRegisterFailed += _parent.OnChildOrderRegisterFailed;
				item.OrderCancelFailed += _parent.OnChildOrderCancelFailed;
				item.NewMyTrade += _parent.AddMyTrade;
				item.OrderReRegistering += _parent.OnOrderReRegistering;
				item.ProcessStateChanged += OnChildProcessStateChanged;
				item.Error += _parent.OnError;

				item.Orders.ForEach(_parent.ProcessOrder);

				if (!item.MyTrades.IsEmpty())
					item.MyTrades.ForEach(_parent.AddMyTrade);

				//_parent._orderFails.AddRange(item.OrderFails);

				if (item.ProcessState == _parent.ProcessState && _parent.ProcessState == ProcessStates.Started)
					OnChildProcessStateChanged(item);
				else
					item.ProcessState = _parent.ProcessState;
			}

			private void OnChildProcessStateChanged(Strategy child)
			{
				if (child.ProcessState == ProcessStates.Started)
				{
					// для предотвращения остановки родительской стратегии пока работают ее дочерние
					var rule =
						child
							.WhenStopped()
							.Do(() => _childStrategyRules.Remove(child))
							.Once()
							.Apply(_parent);

					rule.UpdateName(rule.Name + $" ({nameof(ChildStrategyList)}.{nameof(OnChildProcessStateChanged)})");

					_childStrategyRules.Add(child, rule);
				}
			}

			protected override bool OnClearing()
			{
				foreach (var item in ToArray())
					Remove(item);

				return true;
			}

			protected override bool OnRemoving(Strategy item)
			{
				//item.Parent = null;

				item.OrderRegistering -= _parent.ProcessChildOrderRegistering;
				item.OrderRegistered -= _parent.ProcessOrder;
				//item.ReRegistering -= _parent.ReRegisterSlippage;
				item.OrderChanged -= _parent.OnChildOrderChanged;
				item.OrderRegisterFailed -= _parent.OnChildOrderRegisterFailed;
				item.OrderCancelFailed -= _parent.OnChildOrderCancelFailed;
				item.OrderCanceling -= _parent.OnOrderCanceling;
				item.NewMyTrade -= _parent.AddMyTrade;
				item.OrderReRegistering -= _parent.OnOrderReRegistering;
				item.ProcessStateChanged -= OnChildProcessStateChanged;
				item.Error -= _parent.OnError;

				var rule = _childStrategyRules.TryGetValue(item);

				if (rule != null)
				{
					// правило могло быть удалено при остановке дочерней стратегии, но перед ее удалением из коллекции у родителя
					if (rule.IsReady)
						_parent.TryRemoveRule(rule);

					_childStrategyRules.Remove(item);	
				}

				return base.OnRemoving(item);
			}

			public void TryRemoveStoppedRule(IMarketRule rule)
			{
				if (rule.Token is Strategy child)
					_childStrategyRules.Remove(child);
			}
		}

		private sealed class StrategyRuleList : MarketRuleList
		{
			private readonly Strategy _strategy;

			public StrategyRuleList(Strategy strategy)
				: base(strategy)
			{
				_strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
			}

			protected override bool OnAdding(IMarketRule item)
			{
				return _strategy.ProcessState != ProcessStates.Stopping && base.OnAdding(item);
			}
		}

		private sealed class OrderInfo
		{
			public OrderInfo()
			{
				PrevState = OrderStates.None;
			}

			public bool IsOwn { get; set; }
			public bool IsCanceled { get; set; }
			public decimal ReceivedVolume { get; set; }
			public OrderFail RegistrationFail { get; set; }
			//public OrderFail CancelationFail { get; set; }
			public OrderStates PrevState { get; set; }
		}

		private readonly CachedSynchronizedDictionary<Order, OrderInfo> _ordersInfo = new CachedSynchronizedDictionary<Order, OrderInfo>();

		private DateTimeOffset _firstOrderTime;
		private DateTimeOffset _lastOrderTime;
		private TimeSpan _maxOrdersKeepTime;
		private DateTimeOffset _lastPnlRefreshTime;
		private DateTimeOffset _prevTradeDate;
		private bool _isPrevDateTradable;

		private string _idStr;

		/// <summary>
		/// Initializes a new instance of the <see cref="Strategy"/>.
		/// </summary>
		public Strategy()
		{
			_childStrategies = new ChildStrategyList(this);

			Rules = new StrategyRuleList(this);

			NameGenerator = new StrategyNameGenerator(this);
			NameGenerator.Changed += name => _name.Value = name;

			_id = this.Param(nameof(Id), base.Id);
			_volume = this.Param<decimal>(nameof(Volume), 1);
			_name = this.Param(nameof(Name), new string(GetType().Name.Where(char.IsUpper).ToArray()));
			_maxErrorCount = this.Param(nameof(MaxErrorCount), 1);
			_disposeOnStop = this.Param(nameof(DisposeOnStop), false);
			_cancelOrdersWhenStopping = this.Param(nameof(CancelOrdersWhenStopping), true);
			_waitAllTrades = this.Param<bool>(nameof(WaitAllTrades));
			_commentOrders = this.Param<bool>(nameof(CommentOrders));
			_ordersKeepTime = this.Param(nameof(OrdersKeepTime), TimeSpan.FromDays(1));
			_logLevel = this.Param(nameof(LogLevel), LogLevels.Inherit);
			_stopOnChildStrategyErrors = this.Param(nameof(StopOnChildStrategyErrors), false);

			InitMaxOrdersKeepTime();

			_strategyStat.Add(this);

			RiskManager = new RiskManager { Parent = this };

			PositionManager = new PositionManager(true);
		}

		private readonly StrategyParam<Guid> _id;

		/// <summary>
		/// Strategy ID.
		/// </summary>
		public override Guid Id
		{
			get => _id.Value;
			set => _id.Value = value;
		}

		private readonly StrategyParam<LogLevels> _logLevel;

		/// <inheritdoc />
		[CategoryLoc(LocalizedStrings.LoggingKey)]
		//[PropertyOrder(8)]
		[DisplayNameLoc(LocalizedStrings.Str9Key)]
		[DescriptionLoc(LocalizedStrings.Str1358Key)]
		public override LogLevels LogLevel
		{
			get => _logLevel.Value;
			set => _logLevel.Value = value;
		}

		private readonly StrategyParam<string> _name;

		/// <summary>
		/// Strategy name.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.NameKey,
			Description = LocalizedStrings.Str1359Key,
			GroupName = LocalizedStrings.GeneralKey,
			Order = 0)]
		public override string Name
		{
			get => _name.Value;
			set
			{
				if (value == Name)
					return;

				NameGenerator.Value = value;
				_name.Value = value;
			}
		}

		/// <summary>
		/// The generator of strategy name.
		/// </summary>
		[Browsable(false)]
		public StrategyNameGenerator NameGenerator { get; }

		private IConnector _connector;

		/// <summary>
		/// Connection to the trading system.
		/// </summary>
		[Browsable(false)]
		public virtual IConnector Connector
		{
			get => _connector;
			set
			{
				if (Connector == value)
					return;

				if (_connector != null)
				{
					_connector.NewOrder -= OnConnectorNewOrder;
					_connector.OrderChanged -= OnConnectorOrderChanged;
					_connector.OrderRegisterFailed -= OnConnectorOrderRegisterFailed;
					_connector.OrderCancelFailed -= ProcessCancelOrderFail;
					_connector.NewMyTrade -= OnConnectorNewMyTrade;
					_connector.NewMessage -= OnConnectorNewMessage;
					_connector.ValuesChanged -= OnConnectorValuesChanged;
					_connector.OrderStatusFailed -= OnConnectorOrderStatusFailed;
					_connector.OrderStatusFailed2 -= OnConnectorOrderStatusFailed2;
					_connector.LookupPortfoliosResult -= OnConnectorLookupPortfoliosResult;
					_connector.LookupPortfoliosResult2 -= OnConnectorLookupPortfoliosResult2;
					_connector.MassOrderCancelFailed -= OnConnectorMassOrderCancelFailed;
					_connector.MassOrderCancelFailed2 -= OnConnectorMassOrderCancelFailed2;
					_connector.MassOrderCanceled -= OnConnectorMassOrderCanceled;
					_connector.MassOrderCanceled2 -= OnConnectorMassOrderCanceled2;
					_connector.NewPortfolio -= OnConnectorNewPortfolio;
					_connector.PortfolioChanged -= OnConnectorPortfolioChanged;
				}

				_connector = value;

				if (_connector != null)
				{
					_connector.NewOrder += OnConnectorNewOrder;
					_connector.OrderChanged += OnConnectorOrderChanged;
					_connector.OrderRegisterFailed += OnConnectorOrderRegisterFailed;
					_connector.OrderCancelFailed += ProcessCancelOrderFail;
					_connector.NewMyTrade += OnConnectorNewMyTrade;
					_connector.NewMessage += OnConnectorNewMessage;
					_connector.ValuesChanged += OnConnectorValuesChanged;
					_connector.OrderStatusFailed += OnConnectorOrderStatusFailed;
					_connector.OrderStatusFailed2 += OnConnectorOrderStatusFailed2;
					_connector.LookupPortfoliosResult += OnConnectorLookupPortfoliosResult;
					_connector.LookupPortfoliosResult2 += OnConnectorLookupPortfoliosResult2;
					_connector.MassOrderCancelFailed += OnConnectorMassOrderCancelFailed;
					_connector.MassOrderCancelFailed2 += OnConnectorMassOrderCancelFailed2;
					_connector.MassOrderCanceled += OnConnectorMassOrderCanceled;
					_connector.MassOrderCanceled2 += OnConnectorMassOrderCanceled2;
					_connector.NewPortfolio += OnConnectorNewPortfolio;
					_connector.PortfolioChanged += OnConnectorPortfolioChanged;
				}

				foreach (var strategy in ChildStrategies)
					strategy.Connector = value;

				ConnectorChanged?.Invoke();
			}
		}

		/// <summary>
		/// To get the strategy getting <see cref="Connector"/>. If it is not initialized, the exception will be discarded.
		/// </summary>
		/// <returns>Connector.</returns>
		public IConnector SafeGetConnector()
		{
			var connector = Connector;

			if (connector == null)
				throw new InvalidOperationException(LocalizedStrings.Str1360);

			return connector;
		}

		private Portfolio _portfolio;

		/// <summary>
		/// Portfolio.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.PortfolioKey,
			Description = LocalizedStrings.Str1361Key,
			GroupName = LocalizedStrings.GeneralKey,
			Order = 1)]
		public virtual Portfolio Portfolio
		{
			get => _portfolio;
			set
			{
				if (_portfolio == value)
					return;

				_portfolio = value;

				foreach (var strategy in ChildStrategies)
				{
					if (strategy.Portfolio == null)
						strategy.Portfolio = value;
				}

				RaiseParametersChanged(nameof(Portfolio));
			}
		}

		private Security _security;

		/// <summary>
		/// Security.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.SecurityKey,
			Description = LocalizedStrings.Str1362Key,
			GroupName = LocalizedStrings.GeneralKey,
			Order = 2)]
		public virtual Security Security
		{
			get => _security;
			set
			{
				if (_security == value)
					return;

				_security = value;

				foreach (var strategy in ChildStrategies)
				{
					if (strategy.Security == null)
						strategy.Security = value;
				}
				
				RaiseParametersChanged(nameof(Security));

				PositionManager.SecurityId = value?.ToSecurityId();
			}
		}

		/// <summary>
		/// Total slippage.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.Str163Key,
			Description = LocalizedStrings.Str1363Key,
			GroupName = LocalizedStrings.Str436Key,
			Order = 99)]
		[ReadOnly(true)]
		[Browsable(false)]
		public decimal? Slippage { get; private set; }

		/// <summary>
		/// <see cref="Strategy.Slippage"/> change event.
		/// </summary>
		public event Action SlippageChanged;

		private IPnLManager _pnLManager = new PnLManager { UseOrderBook = true };

		/// <summary>
		/// The profit-loss manager. It accounts trades of this strategy, as well as of its subsidiary strategies <see cref="Strategy.ChildStrategies"/>.
		/// </summary>
		[Browsable(false)]
		public IPnLManager PnLManager
		{
			get => _pnLManager;
			set => _pnLManager = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>
		/// The aggregate value of profit-loss without accounting commission <see cref="Strategy.Commission"/>.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.PnLKey,
			Description = LocalizedStrings.Str1364Key,
			GroupName = LocalizedStrings.Str436Key,
			Order = 100)]
		[ReadOnly(true)]
		[Browsable(false)]
		public decimal PnL => PnLManager.PnL;

		/// <summary>
		/// <see cref="PnL"/> change event.
		/// </summary>
		public event Action PnLChanged;

		/// <summary>
		/// Total commission.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.Str159Key,
			Description = LocalizedStrings.Str1365Key,
			GroupName = LocalizedStrings.Str436Key,
			Order = 101)]
		[ReadOnly(true)]
		[Browsable(false)]
		public decimal? Commission { get; private set; }

		/// <summary>
		/// <see cref="Commission"/> change event.
		/// </summary>
		public event Action CommissionChanged;

		private IPositionManager _positionManager;

		/// <summary>
		/// The position manager. It accounts trades of this strategy, as well as of its subsidiary strategies <see cref="Strategy.ChildStrategies"/>.
		/// </summary>
		[Browsable(false)]
		public IPositionManager PositionManager
		{
			get => _positionManager;
			set
			{
				if (value == null)
					throw new ArgumentNullException(nameof(value));

				if (_positionManager != null)
				{
					_positionManager.NewPosition -= PositionManager_OnNewPosition;
					_positionManager.PositionChanged -= PositionManager_OnPositionChanged;
				}

				_positionManager = value;

				_positionManager.NewPosition += PositionManager_OnNewPosition;
				_positionManager.PositionChanged += PositionManager_OnPositionChanged;
			}
		}

		private void PositionManager_OnNewPosition(Tuple<SecurityId, string> key, decimal value)
		{
			_newPosition?.Invoke(ProcessPositionInfo(key, value));
		}

		private void PositionManager_OnPositionChanged(Tuple<SecurityId, string> key, decimal value)
		{
			_positionChanged?.Invoke(ProcessPositionInfo(key, value));
		}

		/// <summary>
		/// The position aggregate value.
		/// </summary>
		[Browsable(false)]
		public decimal Position
		{
			get => PositionManager.Position;
			set
			{
				if (Position == value)
					return;

				PositionManager.Position = value;
				RaisePositionChanged();
			}
		}

		/// <summary>
		/// <see cref="Position"/> change event.
		/// </summary>
		public event Action PositionChanged;

		/// <summary>
		/// Total latency.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.Str161Key,
			Description = LocalizedStrings.Str1366Key,
			GroupName = LocalizedStrings.Str436Key,
			Order = 102)]
		[ReadOnly(true)]
		[Browsable(false)]
		public TimeSpan? Latency { get; private set; }

		/// <summary>
		/// <see cref="Latency"/> change event.
		/// </summary>
		public event Action LatencyChanged;

		private StatisticManager _statisticManager = new StatisticManager();

		/// <summary>
		/// The statistics manager.
		/// </summary>
		[Browsable(false)]
		public StatisticManager StatisticManager
		{
			get => _statisticManager;
			protected set => _statisticManager = value ?? throw new ArgumentNullException(nameof(value));
		}

		private IRiskManager _riskManager;

		/// <summary>
		/// The risks control manager.
		/// </summary>
		[Browsable(false)]
		public IRiskManager RiskManager
		{
			get => _riskManager;
			set => _riskManager = value ?? throw new ArgumentNullException(nameof(value));
		}

		/// <summary>
		/// Strategy parameters.
		/// </summary>
		[Browsable(false)]
		public CachedSynchronizedDictionary<string, IStrategyParam> Parameters { get; } = new CachedSynchronizedDictionary<string, IStrategyParam>(StringComparer.InvariantCultureIgnoreCase);

		/// <summary>
		/// <see cref="Parameters"/> change event.
		/// </summary>
		public event Action ParametersChanged;

		/// <summary>
		/// To call events <see cref="ParametersChanged"/> and <see cref="PropertyChanged"/>.
		/// </summary>
		/// <param name="name">Parameter name.</param>
		protected internal void RaiseParametersChanged(string name)
		{
			ParametersChanged?.Invoke();
			this.Notify(name);
		}

		/// <summary>
		/// Strategy environment parameters.
		/// </summary>
		[Browsable(false)]
		public SettingsStorage Environment { get; } = new SettingsStorage();

		private readonly StrategyParam<int> _maxErrorCount;

		/// <summary>
		/// The maximal number of errors, which strategy shall receive prior to stop operation.
		/// </summary>
		/// <remarks>
		/// The default value is 1.
		/// </remarks>
		[Browsable(false)]
		public int MaxErrorCount
		{
			get => _maxErrorCount.Value;
			set
			{
				if (value < 1)
					throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.Str1367);

				_maxErrorCount.Value = value;
			}
		}

		private int _errorCount;

		/// <summary>
		/// The current number of errors.
		/// </summary>
		[Browsable(false)]
		public int ErrorCount
		{
			get => _errorCount;
			private set
			{
				if (_errorCount == value)
					return;

				_errorCount = value;
				this.Notify(nameof(ErrorCount));
			}
		}

		private ProcessStates _processState;

		/// <summary>
		/// The operation state.
		/// </summary>
		[Browsable(false)]
		public virtual ProcessStates ProcessState
		{
			get => _processState;
			private set
			{
				if (_processState == value)
					return;

				this.AddDebugLog(LocalizedStrings.Str1368Params, _processState, value);

				if (_processState == ProcessStates.Stopped && value == ProcessStates.Stopping)
					throw new InvalidOperationException(LocalizedStrings.Str1369Params.Put(Name, value));

				_processState = value;

				try
				{
					var child = (IEnumerable<Strategy>)ChildStrategies;

					if (ProcessState == ProcessStates.Stopping)
						child = child.Where(s => s.ProcessState == ProcessStates.Started);

					child.ToArray().ForEach(s => s.ProcessState = ProcessState);

					switch (value)
					{
						case ProcessStates.Started:
						{
							StartedTime = CurrentTime;
							LogProcessState(value);
							OnStarted();
							break;
						}
						case ProcessStates.Stopping:
						{
							LogProcessState(value);
							OnStopping();
							break;
						}
						case ProcessStates.Stopped:
						{
							TotalWorkingTime += CurrentTime - StartedTime;
							StartedTime = default;
							LogProcessState(value);
							OnStopped();
							break;
						}
					}
				}
				catch (Exception error)
				{
					OnError(this, error);
				}

				try
				{
					RaiseProcessStateChanged(this);
					this.Notify(nameof(ProcessState));
				}
				catch (Exception error)
				{
					OnError(this, error);
				}
				
				if (ProcessState == ProcessStates.Stopping)
				{
					if (CancelOrdersWhenStopping)
					{
						this.AddInfoLog(LocalizedStrings.Str1370);
						ProcessCancelActiveOrders();
					}

					foreach (var rule in Rules.ToArray())
					{
						if (this.TryRemoveWithExclusive(rule))
							_childStrategies.TryRemoveStoppedRule(rule);
					}

					TryFinalStop();
				}

				RaiseNewStateMessage(nameof(ProcessState), ProcessState);
			}
		}

		private void LogProcessState(ProcessStates state)
		{
			string stateStr;

			switch (state)
			{
				case ProcessStates.Stopped:
					stateStr = LocalizedStrings.Str1371;
					break;
				case ProcessStates.Stopping:
					stateStr = LocalizedStrings.Str1372;
					break;
				case ProcessStates.Started:
					stateStr = LocalizedStrings.Str1373;
					break;
				default:
					throw new ArgumentOutOfRangeException(nameof(state), state, LocalizedStrings.Str1219);
			}

			this.AddInfoLog(LocalizedStrings.Str1374Params, stateStr, ChildStrategies.Count, Parent != null ? ParentStrategy.ChildStrategies.Count : -1, Position);
		}

		private Strategy ParentStrategy => (Strategy)Parent;

		/// <summary>
		/// <see cref="Strategy.ProcessState"/> change event.
		/// </summary>
		public event Action<Strategy> ProcessStateChanged;

		/// <summary>
		/// To call the event <see cref="Strategy.ProcessStateChanged"/>.
		/// </summary>
		/// <param name="strategy">Strategy.</param>
		protected void RaiseProcessStateChanged(Strategy strategy)
		{
			if (strategy == null)
				throw new ArgumentNullException(nameof(strategy));

			ProcessStateChanged?.Invoke(strategy);
		}

		private readonly StrategyParam<bool> _cancelOrdersWhenStopping;

		/// <summary>
		/// To cancel active orders at stop. Is On by default.
		/// </summary>
		[Browsable(false)]
		public virtual bool CancelOrdersWhenStopping
		{
			get => _cancelOrdersWhenStopping.Value;
			set => _cancelOrdersWhenStopping.Value = value;
		}

		/// <summary>
		/// Orders, registered within the strategy framework.
		/// </summary>
		[Browsable(false)]
		public IEnumerable<Order> Orders => _ordersInfo.CachedKeys;

		/// <summary>
		/// Stop-orders, registered within the strategy framework.
		/// </summary>
		[Browsable(false)]
		[Obsolete("Use Orders property.")]
		public IEnumerable<Order> StopOrders => Orders.Where(o => o.Type == OrderTypes.Conditional);

		private readonly StrategyParam<TimeSpan> _ordersKeepTime;

		/// <summary>
		/// The time for storing <see cref="Orders"/> and <see cref="StopOrders"/> orders in memory. By default it equals to 2 days. If value is set in <see cref="TimeSpan.Zero"/>, orders will not be deleted.
		/// </summary>
		[Browsable(false)]
		public TimeSpan OrdersKeepTime
		{
			get => _ordersKeepTime.Value;
			set
			{
				if (value < TimeSpan.Zero)
					throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.Str1375);

				_ordersKeepTime.Value = value;
				InitMaxOrdersKeepTime();
				RecycleOrders();
			}
		}

		private void InitMaxOrdersKeepTime()
		{
			_maxOrdersKeepTime = TimeSpan.FromTicks((long)(OrdersKeepTime.Ticks * 1.5));
		}

		private readonly CachedSynchronizedSet<MyTrade> _myTrades = new CachedSynchronizedSet<MyTrade> { ThrowIfDuplicate = true };

		/// <summary>
		/// Trades, matched during the strategy operation.
		/// </summary>
		[Browsable(false)]
		public IEnumerable<MyTrade> MyTrades => _myTrades.Cache;

		/// <summary>
		/// Orders with errors, registered within the strategy.
		/// </summary>
		[Browsable(false)]
		public IEnumerable<OrderFail> OrderFails => _ordersInfo.CachedValues.Where(i => i.RegistrationFail != null).Select(i => i.RegistrationFail);

		private readonly StrategyParam<decimal> _volume;

		/// <summary>
		/// Operational volume.
		/// </summary>
		/// <remarks>
		/// If the value is set 0, the parameter is ignored.
		/// </remarks>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.VolumeKey,
			Description = LocalizedStrings.Str1376Key,
			GroupName = LocalizedStrings.GeneralKey,
			Order = 4)]
		public virtual decimal Volume
		{
			get => _volume.Value;
			set
			{
				if (value < 0)
					throw new ArgumentOutOfRangeException(nameof(value), value, LocalizedStrings.Str1377);

				_volume.Value = value;
			}
		}

		private LogLevels _errorState;

		/// <summary>
		/// The state of an error.
		/// </summary>
		[Browsable(false)]
		public LogLevels ErrorState
		{
			get => _errorState;
			private set
			{
				if (_errorState == value)
					return;

				_errorState = value;
				this.Notify(nameof(ErrorState));
			}
		}

		private readonly ChildStrategyList _childStrategies;

		/// <summary>
		/// Subsidiary trade strategies.
		/// </summary>
		[Browsable(false)]
		public IStrategyChildStrategyList ChildStrategies => _childStrategies;

		private DateTimeOffset _startedTime;

		/// <summary>
		/// Strategy start time.
		/// </summary>
		[Display(
			ResourceType = typeof(LocalizedStrings),
			Name = LocalizedStrings.Str1378Key,
			Description = LocalizedStrings.Str1379Key,
			GroupName = LocalizedStrings.Str436Key,
			Order = 105)]
		[ReadOnly(true)]
		[Browsable(false)]
		public DateTimeOffset StartedTime
		{
			get => _startedTime;
			private set
			{
				_startedTime = value;
				this.Notify(nameof(StartedTime));
			}
		}

		private TimeSpan _totalWorkingTime;

		/// <summary>
		/// The total time of strategy operation less time periods, when strategy was stopped.
		/// </summary>
		[Browsable(false)]
		public TimeSpan TotalWorkingTime
		{
			get
			{
				var retVal = _totalWorkingTime;

				if (StartedTime != default && Connector != null)
					retVal += CurrentTime - StartedTime;

				return retVal;
			}
			private set
			{
				if (_totalWorkingTime == value)
					return;

				_totalWorkingTime = value;
				this.Notify(nameof(TotalWorkingTime));
			}
		}

		private readonly StrategyParam<bool> _disposeOnStop;

		/// <summary>
		/// Automatically to clear resources, used by the strategy, when it stops (state <see cref="Strategy.ProcessState"/> becomes equal to <see cref="ProcessStates.Stopped"/>) and delete it from the parent strategy through <see cref="Strategy.ChildStrategies"/>.
		/// </summary>
		/// <remarks>
		/// The mode is used only for one-time strategies, i.e. for those strategies, which will not be started again (for example, quoting). It is disabled by default.
		/// </remarks>
		[Browsable(false)]
		public bool DisposeOnStop
		{
			get => _disposeOnStop.Value;
			set => _disposeOnStop.Value = value;
		}

		private readonly StrategyParam<bool> _waitAllTrades;

		/// <summary>
		/// Stop strategy only after getting all trades by registered orders.
		/// </summary>
		/// <remarks>
		/// It is disabled by default.
		/// </remarks>
		[Browsable(false)]
		public bool WaitAllTrades
		{
			get => _waitAllTrades.Value;
			set => _waitAllTrades.Value = value;
		}

		private readonly StrategyParam<bool> _commentOrders;

		/// <summary>
		/// To add to <see cref="Order.Comment"/> the name of the strategy <see cref="Strategy.Name"/>, registering the order.
		/// </summary>
		/// <remarks>
		/// It is disabled by default.
		/// </remarks>
		[Browsable(false)]
		public bool CommentOrders
		{
			get => _commentOrders.Value;
			set => _commentOrders.Value = value;
		}

		/// <inheritdoc />
		[Browsable(false)]
		public IMarketRuleList Rules { get; }

		//private readonly object _rulesSuspendLock = new object();
		private int _rulesSuspendCount;

		/// <inheritdoc />
		[Browsable(false)]
		public bool IsRulesSuspended => _rulesSuspendCount > 0;

		private readonly StrategyParam<bool> _stopOnChildStrategyErrors;

		/// <summary>
		/// Stop strategy when child strategies causes errors.
		/// </summary>
		/// <remarks>
		/// It is disabled by default.
		/// </remarks>
		[Browsable(false)]
		public bool StopOnChildStrategyErrors
		{
			get => _stopOnChildStrategyErrors.Value;
			set => _stopOnChildStrategyErrors.Value = value;
		}

		/// <summary>
		/// The event of sending order for registration.
		/// </summary>
		public event Action<Order> OrderRegistering;

		/// <summary>
		/// The event of order successful registration.
		/// </summary>
		public event Action<Order> OrderRegistered;

		/// <inheritdoc />
		public event Action<OrderFail> OrderRegisterFailed;

		/// <summary>
		/// The event of sending order for re-registration.
		/// </summary>
		public event Action<Order, Order> OrderReRegistering;

		/// <inheritdoc />
		public event Action<OrderFail> OrderCancelFailed;

		/// <summary>
		/// The event of sending order for cancelling.
		/// </summary>
		public event Action<Order> OrderCanceling;

		/// <inheritdoc />
		public event Action<Order> OrderChanged;

		/// <inheritdoc />
		[Obsolete("Use OrderRegisterFailed event.")]
		public event Action<OrderFail> StopOrderRegisterFailed;

		/// <inheritdoc />
		[Obsolete("Use OrderChanged event.")]
		public event Action<Order> StopOrderChanged;

#pragma warning disable 67
		/// <summary>
		/// The event of sending stop-order for registration.
		/// </summary>
		[Obsolete("Use OrderRegistering event.")]
		public event Action<Order> StopOrderRegistering;

		/// <summary>
		/// The event of stop-order successful registration.
		/// </summary>
		[Obsolete("Use OrderRegistered event.")]
		public event Action<Order> StopOrderRegistered;

		/// <summary>
		/// The event of sending stop-order for cancelling.
		/// </summary>
		[Obsolete("Use OrderCanceling event.")]
		public event Action<Order> StopOrderCanceling;

		/// <summary>
		/// The event of sending stop-order for re-registration.
		/// </summary>
		[Obsolete("Use OrderReRegistering event.")]
		public event Action<Order, Order> StopOrderReRegistering;
#pragma warning restore 67

		/// <inheritdoc />
		[Obsolete("Use OrderCancelFailed event.")]
		public event Action<OrderFail> StopOrderCancelFailed;

		/// <inheritdoc />
		public event Action<MyTrade> NewMyTrade;

		/// <summary>
		/// The event of strategy connection change.
		/// </summary>
		public event Action ConnectorChanged;

		/// <summary>
		/// The event of error occurrence in the strategy.
		/// </summary>
		public event Action<Strategy, Exception> Error;

		/// <summary>
		/// The method is called when the <see cref="Start()"/> method has been called and the <see cref="ProcessState"/> state has been taken the <see cref="ProcessStates.Started"/> value.
		/// </summary>
		protected virtual void OnStarted()
		{
			if (Security == null)
				throw new InvalidOperationException(LocalizedStrings.Str1380);

			if (Portfolio == null)
				throw new InvalidOperationException(LocalizedStrings.Str1381);

			InitStartValues();
		}
		
		/// <summary>
		/// Init.
		/// </summary>
		protected void InitStartValues()
		{
			foreach (var parameter in Parameters.CachedValues)
			{
				if (parameter.Value is Unit unit && unit.GetTypeValue == null && (unit.Type == UnitTypes.Point || unit.Type == UnitTypes.Step))
					unit.SetSecurity(Security);
			}

			ErrorCount = 0;
			ErrorState = LogLevels.Info;
		}

		/// <summary>
		/// The method is called when the <see cref="ProcessState"/> process state has been taken the <see cref="ProcessStates.Stopping"/> value.
		/// </summary>
		protected virtual void OnStopping()
		{
		}

		/// <summary>
		/// The method is called when the <see cref="ProcessState"/> process state has been taken the <see cref="ProcessStates.Stopped"/> value.
		/// </summary>
		protected virtual void OnStopped()
		{
		}

		/// <inheritdoc />
		public virtual void RegisterOrder(Order order)
		{
			if (order == null)
				throw new ArgumentNullException(nameof(order));

			this.AddInfoLog(LocalizedStrings.Str1382Params,
				order.Type, order.Direction, order.Price, order.Volume, order.Comment, order.GetHashCode());

			if (ProcessState != ProcessStates.Started)
			{
				this.AddWarningLog(LocalizedStrings.Str1383Params, ProcessState);
				return;
			}

			if (order.Security == null)
				order.Security = Security;

			if (order.Portfolio == null)
				order.Portfolio = Portfolio;

			if (CommentOrders)
			{
				if (order.Comment.IsEmpty())
					order.Comment = Name;
			}

			AddOrder(order);

			ProcessRegisterOrderAction(null, order, (oOrder, nOrder) =>
			{
				OnOrderRegistering(nOrder);

				SafeGetConnector().RegisterOrder(nOrder);
			});
		}

		/// <inheritdoc />
		public virtual void ReRegisterOrder(Order oldOrder, Order newOrder)
		{
			if (oldOrder == null)
				throw new ArgumentNullException(nameof(oldOrder));

			if (newOrder == null)
				throw new ArgumentNullException(nameof(newOrder));

			this.AddInfoLog(LocalizedStrings.Str1384Params, oldOrder.TransactionId, oldOrder.Price, newOrder.Price, oldOrder.Comment);

			if (ProcessState != ProcessStates.Started)
			{
				this.AddWarningLog(LocalizedStrings.Str1385Params, ProcessState);
				return;
			}

			AddOrder(newOrder);

			ProcessRegisterOrderAction(oldOrder, newOrder, (oOrder, nOrder) =>
			{
				OnOrderReRegistering(oOrder, nOrder);

				//ReRegisterSlippage(oOrder, nOrder);

				SafeGetConnector().ReRegisterOrder(oOrder, nOrder);	
			});
		}

		private void ProcessRisk(Order order)
		{
			ProcessRisk(order.CreateRegisterMessage());
		}

		private void ProcessChildOrderRegistering(Order order)
		{
			OnOrderRegistering(order);

			_newOrder?.Invoke(order);

			ProcessRisk(order);
		}

		private void AddOrder(Order order)
		{
			ProcessRisk(order);

			_ordersInfo.Add(order, new OrderInfo { IsOwn = true });

			if (!order.State.IsFinal())
				ApplyMonitorRules(order);

			_newOrder?.Invoke(order);
		}

		private void ProcessRegisterOrderAction(Order oOrder, Order nOrder, Action<Order, Order> action)
		{
			try
			{
				action(oOrder, nOrder);
			}
			catch (Exception excp)
			{
				Rules.RemoveRulesByToken(nOrder, null);

				nOrder.State = nOrder.State.CheckModification(OrderStates.Failed);

				var fail = new OrderFail { Order = nOrder, Error = excp, ServerTime = CurrentTime };

				OnConnectorOrderRegisterFailed(fail);
			}
		}

		private void ApplyMonitorRules(Order order)
		{
			if (!CancelOrdersWhenStopping)
				return;

			IMarketRule matchedRule = order.WhenMatched(this);

			if (WaitAllTrades)
				matchedRule = matchedRule.And(order.WhenAllTrades(this));

			var successRule = order
				.WhenCanceled(this)
				.Or(matchedRule, order.WhenRegisterFailed(this))
				.Do(() => this.AddInfoLog(LocalizedStrings.Str1386Params.Put(order.TransactionId)))
				.Until(() =>
				{
					if (order.State == OrderStates.Failed)
						return true;

					if (order.State != OrderStates.Done)
					{
						this.AddWarningLog(LocalizedStrings.OrderHasState, order.TransactionId, order.State);
						return false;
					}

					if (!WaitAllTrades)
						return true;

					if (!_ordersInfo.TryGetValue(order, out var info))
					{
						this.AddWarningLog(LocalizedStrings.Str1156Params, order.TransactionId);
						return false;
					}

					var leftVolume = order.GetMatchedVolume() - info.ReceivedVolume;

					if (leftVolume != 0)
					{					
						this.AddDebugLog(LocalizedStrings.OrderHasBalance, order.TransactionId, leftVolume);
						return false;
					}

					return true;
				})
				.Apply(this);

			var canFinish = false;

			order
				.WhenCancelFailed(this)
				.Do(() =>
				{
					if (ProcessState != ProcessStates.Stopping)
						return;

					canFinish = true;
					this.AddInfoLog(LocalizedStrings.Str1387Params.Put(order.TransactionId));
				})
				.Until(() => canFinish)
				.Apply(this)
				.Exclusive(successRule);
		}

		/// <inheritdoc />
		public virtual void CancelOrder(Order order)
		{
			if (ProcessState != ProcessStates.Started)
			{
				this.AddWarningLog(LocalizedStrings.Str1388Params, ProcessState);
				return;
			}

			if (order == null)
				throw new ArgumentNullException(nameof(order));

			lock (_ordersInfo.SyncRoot)
			{
				var info = _ordersInfo.TryGetValue(order);

				if (info == null || !info.IsOwn)
					throw new ArgumentException(LocalizedStrings.Str1389Params.Put(order.TransactionId, Name));

				if (info.IsCanceled)
				{
					this.AddWarningLog(LocalizedStrings.Str1390Params, order.TransactionId);
					return;
				}

				info.IsCanceled = true;
			}

			CancelOrderHandler(order);
		}

		private void CancelOrderHandler(Order order)
		{
			if (order == null)
				throw new ArgumentNullException(nameof(order));

			this.AddInfoLog(LocalizedStrings.Str1315Params, order.TransactionId);

			OnOrderCanceling(order);

			SafeGetConnector().CancelOrder(order);
		}

		/// <summary>
		/// To add the order to the strategy.
		/// </summary>
		/// <param name="order">Order.</param>
		private void ProcessOrder(Order order)
		{
			ProcessOrder(order, false);
		}

		/// <summary>
		/// To add the order to the strategy.
		/// </summary>
		/// <param name="order">Order.</param>
		/// <param name="isChanging">The order came from the change event.</param>
		private void ProcessOrder(Order order, bool isChanging)
		{
			if (order == null)
				throw new ArgumentNullException(nameof(order));

			var info = _ordersInfo.TryGetValue(order);

			var isRegistered = (info != null && !info.IsOwn && !isChanging) || //иначе не добавляются заявки дочерних стратегий
			                   info != null && info.IsOwn && info.PrevState == OrderStates.None && order.State != OrderStates.Pending;

			if (info != null && info.IsOwn)
				info.PrevState = order.State;

			TryInvoke(() =>
			{
				if (isRegistered)
				{
					var isLatChanged = order.LatencyRegistration != null;

					if (isLatChanged)
					{
						if (Latency == null)
							Latency = TimeSpan.Zero;
							
						Latency += order.LatencyRegistration.Value;
					}

					if (order.Type == OrderTypes.Conditional)
					{
						//_stopOrders.Add(order);
						OnOrderRegistered(order);

						StatisticManager.AddNewOrder(order);
					}
					else
					{
						//_orders.Add(order);
						OnOrderRegistered(order);

						//SlippageManager.Registered(order);

						var pos = PositionManager.ProcessMessage(order.ToMessage());

						StatisticManager.AddNewOrder(order);

						if (order.Commission != null)
						{
							Commission += order.Commission;
							RaiseCommissionChanged();
						}

						if (pos != null)
							RaisePositionChanged();
					}

					if (_firstOrderTime == default)
						_firstOrderTime = order.Time;

					_lastOrderTime = order.Time;

					RecycleOrders();

					if (isLatChanged)
						RaiseLatencyChanged();

					if (ProcessState == ProcessStates.Stopping && CancelOrdersWhenStopping)
					{
						lock (_ordersInfo.SyncRoot)
						{
							//var info = _ordersInfo.TryGetValue(order);

							// заявка принадлежит дочерней стратегии
							if (info == null || !info.IsOwn)
								return;

							// для заявки уже был послан сигнал на снятие
							if (info.IsCanceled)
								return;

							info.IsCanceled = true;
						}

						CancelOrderHandler(order);
					}
				}
				else if (isChanging)
				{
					var pos = PositionManager.ProcessMessage(order.ToMessage());
					StatisticManager.AddChangedOrder(order);

					OnOrderChanged(order);

					if (pos != null)
						RaisePositionChanged();
				}
			});
		}

		/// <summary>
		/// To add the active order to the strategy and process trades by the order.
		/// </summary>
		/// <param name="order">Order.</param>
		/// <param name="myTrades">Trades for order.</param>
		/// <remarks>
		/// It is used to restore a state of the strategy, when it is necessary to subscribe for getting data on orders, registered earlier.
		/// </remarks>
		public virtual void AttachOrder(Order order, IEnumerable<MyTrade> myTrades)
		{
			if (order == null)
				throw new ArgumentNullException(nameof(order));

			if (myTrades == null)
				throw new ArgumentNullException(nameof(myTrades));

			AttachOrder(order);

			myTrades.ForEach(OnConnectorNewMyTrade);
		}

		private void AttachOrder(Order order)
		{
			AddOrder(order);

			if (order.Type != OrderTypes.Conditional)
				PositionManager.ProcessMessage(order.ToMessage());

			ProcessOrder(order);

			OnOrderRegistering(order);
		}

		/// <summary>
		/// To set the strategy identifier for the order.
		/// </summary>
		/// <param name="order">The order, for which the strategy identifier shall be set.</param>
		protected virtual void AssignOrderStrategyId(Order order)
		{
			order.UserOrderId = Id.To<string>();
		}

		private void RecycleOrders()
		{
			if (OrdersKeepTime == TimeSpan.Zero)
				return;

			var diff = _lastOrderTime - _firstOrderTime;

			if (diff <= _maxOrdersKeepTime)
				return;

			_firstOrderTime = _lastOrderTime - OrdersKeepTime;

			_ordersInfo.SyncDo(d => d.RemoveWhere(o => o.Key.State == OrderStates.Done && o.Key.Time < _firstOrderTime));
		}

		/// <summary>
		/// Current time, which will be passed to the <see cref="LogMessage.Time"/>.
		/// </summary>
		public override DateTimeOffset CurrentTime => Connector?.CurrentTime ?? TimeHelper.NowWithOffset;

		/// <inheritdoc />
		protected override void RaiseLog(LogMessage message)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			switch (message.Level)
			{
				case LogLevels.Warning:
					if (ErrorState == LogLevels.Info)
						ErrorState = LogLevels.Warning;
					break;
				case LogLevels.Error:
					ErrorState = LogLevels.Error;
					break;
			}

			// mika
			// так как некоторые стратегии слишком много пишут в лог, то получается слишком медленно
			//
			//TryInvoke(() => base.RaiseLog(message));
			base.RaiseLog(message);
		}

		/// <summary>
		/// To start the trade algorithm.
		/// </summary>
		public virtual void Start()
		{
			SafeGetConnector().SendOutMessage(new StrategyChangeStateMessage(this, ProcessStates.Started));
		}

		/// <summary>
		/// To stop the trade algorithm.
		/// </summary>
		public virtual void Stop()
		{
			SafeGetConnector().SendOutMessage(new StrategyChangeStateMessage(this, ProcessStates.Stopping));
		}

		/// <summary>
		/// The event of the strategy re-initialization.
		/// </summary>
		public event Action Reseted;

		/// <summary>
		/// To call the event <see cref="Reseted"/>.
		/// </summary>
		private void RaiseReseted()
		{
			Reseted?.Invoke();
		}

		/// <summary>
		/// To re-initialize the trade algorithm. It is called after initialization of the strategy object and loading stored parameters.
		/// </summary>
		public virtual void Reset()
		{
			this.AddInfoLog(LocalizedStrings.Str1393);
			
			//ThrowIfTraderNotRegistered();

			//if (Security == null)
			//	throw new InvalidOperationException(LocalizedStrings.Str1380);

			//if (Portfolio == null)
			//	throw new InvalidOperationException(LocalizedStrings.Str1381);

			ChildStrategies.ForEach(s => s.Reset());

			StatisticManager.Reset();

			PnLManager.Reset();
			
			Commission = null;
			//CommissionManager.Reset();

			Latency = null;
			//LatencyManager.Reset();

			PositionManager.Reset();

			Slippage = null;
			//SlippageManager.Reset();

			RiskManager.Reset();

			_myTrades.Clear();
			_ordersInfo.Clear();

			ProcessState = ProcessStates.Stopped;
			ErrorState = LogLevels.Info;
			ErrorCount = 0;

			_firstOrderTime = _lastOrderTime = _lastPnlRefreshTime = _prevTradeDate = default;
			_idStr = null;

			_positions.Clear();

			OnReseted();

			// события вызываем только после вызова Reseted
			// чтобы сбросить состояние у подписчиков стратегии.
			RaisePnLChanged();
			RaiseCommissionChanged();
			RaiseLatencyChanged();
			RaisePositionChanged();
			RaiseSlippageChanged();
		}

		/// <summary>
		/// It is called from the <see cref="Reset"/> method.
		/// </summary>
		protected virtual void OnReseted()
		{
			RaiseReseted();
		}

		void IMarketRuleContainer.SuspendRules()
		{
			_rulesSuspendCount++;

			this.AddDebugLog(LocalizedStrings.Str1394Params, _rulesSuspendCount);
		}

		void IMarketRuleContainer.ResumeRules()
		{
			if (_rulesSuspendCount > 0)
				_rulesSuspendCount--;

			this.AddDebugLog(LocalizedStrings.Str1395Params, _rulesSuspendCount);
		}

		private void TryFinalStop()
		{
			if (!Rules.IsEmpty())
			{
				this.AddLog(LogLevels.Debug,
					() => LocalizedStrings.Str1396Params.Put(Rules.Count, Rules.Select(r => r.ToString()).Join(", ")));

				return;
			}

			ProcessState = ProcessStates.Stopped;

			if (DisposeOnStop)
			{
				//Trace.WriteLine(Name+" strategy-dispose-on-stop");

				if (Parent != null)
					ParentStrategy.ChildStrategies.Remove(this);

				Dispose();
			}
		}

		void IMarketRuleContainer.ActivateRule(IMarketRule rule, Func<bool> process)
		{
			if (_rulesSuspendCount > 0)
			{
				this.AddRuleLog(LogLevels.Debug, rule, LocalizedStrings.Str1397);
				return;
			}

			try
			{
				this.ActiveRule(rule, process);
			}
			catch (Exception error)
			{
				OnError(this, error);
			}
			finally
			{
				if (_processState == ProcessStates.Stopping)
					TryFinalStop();
			}
		}

		private TimeSpan _unrealizedPnLInterval = TimeSpan.FromMinutes(1);

		/// <summary>
		/// The interval for unrealized profit recalculation. The default value is 1 minute.
		/// </summary>
		[Browsable(false)]
		public virtual TimeSpan UnrealizedPnLInterval
		{
			get => _unrealizedPnLInterval;
			set
			{
				if (value <= TimeSpan.Zero)
					throw new ArgumentOutOfRangeException();

				_unrealizedPnLInterval = value;
			}
		}

		/// <summary>
		/// The method, called at occurrence of new strategy trade.
		/// </summary>
		/// <param name="trade">New trade of a strategy.</param>
		protected virtual void OnNewMyTrade(MyTrade trade)
		{
			NewMyTrade?.Invoke(trade);
		}

		/// <summary>
		/// To call the event <see cref="Strategy.OrderRegistering"/>.
		/// </summary>
		/// <param name="order">Order.</param>
		protected virtual void OnOrderRegistering(Order order)
		{
			TryAddChildOrder(order);

			OrderRegistering?.Invoke(order);
			//SlippageManager.Registering(order);
		}

		/// <summary>
		/// To call the event <see cref="Strategy.OrderRegistered"/>.
		/// </summary>
		/// <param name="order">Order.</param>
		protected virtual void OnOrderRegistered(Order order)
		{
			OrderRegistered?.Invoke(order);
		}

		/// <summary>
		/// To call the event <see cref="Strategy.OrderRegistered"/>.
		/// </summary>
		/// <param name="order">Order.</param>
		protected virtual void OnOrderCanceling(Order order)
		{
			OrderCanceling?.Invoke(order);
		}

		/// <summary>
		/// To call the event <see cref="Strategy.OrderReRegistering"/>.
		/// </summary>
		/// <param name="oldOrder">Cancelling order.</param>
		/// <param name="newOrder">New order to register.</param>
		protected virtual void OnOrderReRegistering(Order oldOrder, Order newOrder)
		{
			TryAddChildOrder(newOrder);
			
			OrderReRegistering?.Invoke(oldOrder, newOrder);
		}

		/// <summary>
		/// The method, called at strategy order change.
		/// </summary>
		/// <param name="order">The changed order.</param>
		protected virtual void OnOrderChanged(Order order)
		{
			OrderChanged?.Invoke(order);

			var latency = order.LatencyCancellation;

			if (latency == null)
				return;

			if (Latency == null)
				Latency = TimeSpan.Zero;

			Latency += latency.Value;
			RaiseLatencyChanged();
		}

		/// <summary>
		/// The method, called at strategy order registration error.
		/// </summary>
		/// <param name="fail">Error registering order.</param>
		protected virtual void OnOrderRegisterFailed(OrderFail fail)
		{
			OrderRegisterFailed?.Invoke(fail);
			StatisticManager.AddRegisterFailedOrder(fail);
		}

		private void TryAddChildOrder(Order order)
		{
			lock (_ordersInfo.SyncRoot)
			{
				var info = _ordersInfo.TryGetValue(order);

				if (info == null)
					_ordersInfo.Add(order, new OrderInfo { IsOwn = false });
			}

			AssignOrderStrategyId(order);
		}

		private void OnConnectorNewMessage(Message message)
		{
			DateTimeOffset? msgTime = null;

			switch (message.Type)
			{
				case MessageTypes.QuoteChange:
				{
					// при тестировании на истории в стакане могут быть свои заявки по ценам планок,
					// исключаем эти цены из расчетов нереализованной прибыли
					// (убрать свои заявки из стакана не получается, т.к. заявка могла уже исполниться,
					// но сам стакан еще не обновился и придет только следующим сообщением).

					var quoteMsg = (QuoteChangeMessage)message;

					// TODO на истории когда в стакане будут свои заявки по планкам, то противополжная сторона стакана будет пустой
					// необходимо исключать свои заявки как-то иначе.
					if (quoteMsg.Asks.IsEmpty() || quoteMsg.Bids.IsEmpty())
						return;
					
					PnLManager.ProcessMessage(message);
					msgTime = quoteMsg.ServerTime;

					break;
				}

				case MessageTypes.Level1Change:
					PnLManager.ProcessMessage(message);
					msgTime = ((Level1ChangeMessage)message).ServerTime;
					break;

				case MessageTypes.Execution:
				{
					var execMsg = (ExecutionMessage)message;

					if (execMsg.IsMarketData())
						PnLManager.ProcessMessage(execMsg);

					msgTime = execMsg.ServerTime;
					break;
				}

				case MessageTypes.Time:
					break;

				case ExtendedMessageTypes.StrategyChangeState:
				{
					var stateMsg = (StrategyChangeStateMessage)message;

					if (stateMsg.Strategy == this)
					{
						switch (stateMsg.State)
						{
							//case ProcessStates.Stopped:
							//	break;
							case ProcessStates.Stopping:
							{
								if (ProcessState == ProcessStates.Started)
									ProcessState = ProcessStates.Stopping;
								else
									this.AddDebugLog(LocalizedStrings.Str1392Params, ProcessState);

								break;
							}
							case ProcessStates.Started:
							{
								if (ProcessState == ProcessStates.Stopped)
									ProcessState = ProcessStates.Started;
								else
									this.AddDebugLog(LocalizedStrings.Str1391Params, ProcessState);

								break;
							}
							//default:
							//	throw new ArgumentOutOfRangeException();
						}
					}

					return;
				}

				default:
					return;
			}

			if (msgTime == null || msgTime.Value - _lastPnlRefreshTime < UnrealizedPnLInterval)
				return;

			_lastPnlRefreshTime = msgTime.Value;

			ExchangeBoard board = null;

			if (Security != null && Security.Board != null)
				board = Security.Board;
			else if (Portfolio != null && Portfolio.Board != null)
				board = Portfolio.Board;

			if (board != null)
			{
				var date = _lastPnlRefreshTime.Date;

				if (date != _prevTradeDate)
				{
					_prevTradeDate = date;
					_isPrevDateTradable = board.IsTradeDate(_prevTradeDate);
				}

				if (!_isPrevDateTradable)
					return;

				var period = board.WorkingTime.GetPeriod(date);

				var tod = _lastPnlRefreshTime.TimeOfDay;
				
				if (period != null && !period.Times.IsEmpty() && !period.Times.Any(r => r.Contains(tod)))
					return;
			}

			if (PositionManager.Positions.Any())
				RaisePnLChanged();
		}

		private void OnConnectorNewMyTrade(MyTrade trade)
		{
			if (IsOwnOrder(trade.Order))
				AddMyTrade(trade);
		}

		private void OnConnectorNewOrder(Order order)
		{
			if (_idStr == null)
				_idStr = Id.ToString();

			if (!_ordersInfo.ContainsKey(order) && order.UserOrderId == _idStr)
				AttachOrder(order);
		}

		private void OnConnectorOrderChanged(Order order)
		{
			if (IsOwnOrder(order))
				TryInvoke(() => ProcessOrder(order, true));
		}

		private void OnConnectorOrderRegisterFailed(OrderFail fail)
		{
			ProcessRegisterOrderFail(fail, OnOrderRegisterFailed);
		}

		private void OnConnectorValuesChanged(Security security, IEnumerable<KeyValuePair<Level1Fields, object>> changes, DateTimeOffset serverTime, DateTimeOffset localTime)
		{
			ValuesChanged?.Invoke(security, changes, serverTime, localTime);
		}

		private void UpdatePnLManager(Security security)
		{
			var msg = new Level1ChangeMessage { SecurityId = security.ToSecurityId(), ServerTime = CurrentTime }
					.TryAdd(Level1Fields.PriceStep, security.PriceStep)
					.TryAdd(Level1Fields.StepPrice, this.GetSecurityValue<decimal?>(security, Level1Fields.StepPrice) ?? security.StepPrice)
					.TryAdd(Level1Fields.Multiplier, this.GetSecurityValue<decimal?>(security, Level1Fields.Multiplier) ?? security.Multiplier);

			PnLManager.ProcessMessage(msg);
		}

		private void AddMyTrade(MyTrade trade)
		{
			if (!_myTrades.TryAdd(trade))
				return;

			if (WaitAllTrades)
			{
				lock (_ordersInfo.SyncRoot)
				{
					if (_ordersInfo.TryGetValue(trade.Order, out var info) && info.IsOwn)
						info.ReceivedVolume += trade.Trade.Volume;
				}
			}

			TryInvoke(() => OnNewMyTrade(trade));

			var isComChanged = false;
			var isPnLChanged = false;
			var isSlipChanged = false;

			this.AddInfoLog(LocalizedStrings.Str1398Params,
				trade.Order.Direction,
				(trade.Trade.Id == 0 ? trade.Trade.StringId : trade.Trade.Id.To<string>()),
				trade.Trade.Price, trade.Trade.Volume, trade.Order.TransactionId);

			if (trade.Commission != null)
			{
				if (Commission == null)
					Commission = 0;

				Commission += trade.Commission.Value;
				isComChanged = true;
			}

			UpdatePnLManager(trade.Trade.Security);

			var execMsg = trade.ToMessage();

			var tradeInfo = PnLManager.ProcessMessage(execMsg);
			if (tradeInfo != null)
			{
				if (tradeInfo.PnL != 0)
					isPnLChanged = true;

				StatisticManager.AddMyTrade(tradeInfo);
			}

			var pos = PositionManager.ProcessMessage(execMsg);

			if (trade.Slippage != null)
			{
				if (Slippage == null)
					Slippage = 0;

				Slippage += trade.Slippage.Value;
				isSlipChanged = true;
			}

			TryInvoke(() =>
			{
				if (isComChanged)
					RaiseCommissionChanged();

				if (isPnLChanged)
					RaisePnLChanged();

				if (pos != null)
					RaisePositionChanged();

				if (isSlipChanged)
					RaiseSlippageChanged();
			});

			ProcessRisk(execMsg);
		}

		private void RaiseSlippageChanged()
		{
			this.Notify(nameof(Slippage));
			SlippageChanged?.Invoke();

			RaiseNewStateMessage(nameof(Slippage), Slippage);
		}

		private void RaisePositionChanged()
		{
			this.AddInfoLog(LocalizedStrings.Str1399Params, PositionManager.Positions.Select(pos => pos.Key + "=" + pos.Value).Join(", "));

			this.Notify(nameof(Position));
			PositionChanged?.Invoke();

			StatisticManager.AddPosition(CurrentTime, Position);
			StatisticManager.AddPnL(CurrentTime, PnL);

			RaiseNewStateMessage(nameof(Position), Position);
		}

		private void RaiseCommissionChanged()
		{
			this.Notify(nameof(Commission));
			CommissionChanged?.Invoke();

			RaiseNewStateMessage(nameof(Commission), Commission);
		}

		private void RaisePnLChanged()
		{
			this.Notify(nameof(PnL));
			PnLChanged?.Invoke();

			StatisticManager.AddPnL(_lastPnlRefreshTime, PnL);

			RaiseNewStateMessage(nameof(PnL), PnL);
		}

		private void RaiseLatencyChanged()
		{
			this.Notify(nameof(Latency));
			LatencyChanged?.Invoke();

			RaiseNewStateMessage(nameof(Latency), Latency);
		}

		/// <summary>
		/// To process orders, received for the connection <see cref="Strategy.Connector"/>, and find among them those, belonging to the strategy.
		/// </summary>
		/// <param name="newOrders">New orders.</param>
		/// <returns>Orders, belonging to the strategy.</returns>
		protected virtual IEnumerable<Order> ProcessNewOrders(IEnumerable<Order> newOrders)
		{
			return _ordersInfo.SyncGet(d => newOrders.Where(IsOwnOrder).ToArray());
		}

		/// <inheritdoc />
		public override void Load(SettingsStorage storage)
		{
			var parameters = storage.GetValue<SettingsStorage[]>(nameof(Parameters));

			if (parameters == null)
				return;

			//var dict = Parameters.SyncGet(c => c.ToDictionary(p => p.Name, p => p, StringComparer.InvariantCultureIgnoreCase));

			// в настройках могут быть дополнительные параметры, которые будут добавлены позже
			foreach (var s in parameters)
			{
				var param = Parameters.TryGetValue(s.GetValue<string>(nameof(IStrategyParam.Name)));

				param?.Load(s);
			}

			var pnlStorage = storage.GetValue<SettingsStorage>(nameof(PnLManager));

			if (pnlStorage != null)
				PnLManager.Load(pnlStorage);

			var riskStorage = storage.GetValue<SettingsStorage>(nameof(RiskManager));

			if (riskStorage != null)
				RiskManager.Load(riskStorage);
		}

		/// <inheritdoc />
		public override void Save(SettingsStorage storage)
		{
			storage.SetValue(nameof(Parameters), Parameters.CachedValues.Select(p => p.Save()).ToArray());

			storage.SetValue(nameof(PnLManager), PnLManager.Save());
			storage.SetValue(nameof(RiskManager), RiskManager.Save());
			//storage.SetValue(nameof(StatisticManager), StatisticManager.Save());
			//storage.SetValue(nameof(PositionManager), PositionManager.Save());
		}

		/// <inheritdoc />
		public event PropertyChangedEventHandler PropertyChanged;

		void INotifyPropertyChangedEx.NotifyPropertyChanged(string info)
		{
			PropertyChanged?.Invoke(this, info);
		}

		/// <summary>
		/// To cancel all active orders (to stop and regular).
		/// </summary>
		public void CancelActiveOrders()
		{
			if (ProcessState != ProcessStates.Started)
			{
				this.AddWarningLog(LocalizedStrings.Str1400Params, ProcessState);
				return;
			}

			this.AddInfoLog(LocalizedStrings.Str1401);

			ProcessCancelActiveOrders();
		}

		/// <summary>
		/// To cancel all active orders (to stop and regular).
		/// </summary>
		protected virtual void ProcessCancelActiveOrders()
		{
			_ordersInfo.SyncGet(d => d.Keys.Filter(OrderStates.Active).ToArray()).ForEach(o =>
			{
				var info = _ordersInfo.TryGetValue(o);

				//заявка принадлежит дочерней статегии
				if (!info.IsOwn)
					return;

				if (info.IsCanceled)
				{
					this.AddWarningLog(LocalizedStrings.Str1390Params, o.TransactionId);
					return;
				}

				info.IsCanceled = true;

				CancelOrderHandler(o);
			});
		}

		private void OnChildOrderChanged(Order order)
		{
			ProcessOrder(order, true);
		}

		private void OnChildOrderRegisterFailed(OrderFail fail)
		{
			//SlippageManager.RegisterFailed(fail);
			TryInvoke(() => OrderRegisterFailed?.Invoke(fail));
		}

		private void OnChildOrderCancelFailed(OrderFail fail)
		{
			TryInvoke(() => OrderCancelFailed?.Invoke(fail));
		}

		private void ProcessCancelOrderFail(OrderFail fail)
		{
			var order = fail.Order;

			lock (_ordersInfo.SyncRoot)
			{
				var info = _ordersInfo.TryGetValue(order);

				if (info == null || !info.IsOwn)
					return;

				info.IsCanceled = false;
			}

			this.AddErrorLog(LocalizedStrings.Str1402Params, order.TransactionId, fail.Error);

			OrderCancelFailed?.Invoke(fail);

			StatisticManager.AddFailedOrderCancel(fail);
		}

		private void ProcessRegisterOrderFail(OrderFail fail, Action<OrderFail> evt)
		{
			lock (_ordersInfo.SyncRoot)
			{
				var info = _ordersInfo.TryGetValue(fail.Order);

				if (info == null)
					return;

				info.RegistrationFail = fail;
			}

			this.AddErrorLog(LocalizedStrings.Str1302Params, fail.Order.TransactionId, fail.Error.Message);
			//SlippageManager.RegisterFailed(fail);

			TryInvoke(() => evt?.Invoke(fail));
		}

		/// <summary>
		/// Processing of error, occurred as result of strategy operation.
		/// </summary>
		/// <param name="strategy">Strategy.</param>
		/// <param name="error">Error.</param>
		protected virtual void OnError(Strategy strategy, Exception error)
		{
			ErrorCount++;
			Error?.Invoke(strategy, error);

			if (!StopOnChildStrategyErrors && !Equals(this, strategy))
				return;

			this.AddErrorLog(error.ToString());

			if (ErrorCount >= MaxErrorCount)
				Stop();
		}

		object ICloneable.Clone()
		{
			return Clone();
		}

		/// <summary>
		/// Create a copy of <see cref="Strategy"/>.
		/// </summary>
		/// <returns>Copy.</returns>
		public virtual Strategy Clone()
		{
			var clone = GetType().CreateInstance<Strategy>();
			clone.Connector = Connector;
			clone.Security = Security;
			clone.Portfolio = Portfolio;

			var id = clone.Id;
			clone.Load(this.Save());
			clone.Id = id;

			return clone;
		}

		private void TryInvoke(Action handler)
		{
			//_disposeLock.Read(() =>
			//{
				if (IsDisposed)
					return;

				handler();
			//});
		}

		private bool IsOwnOrder(Order order)
		{
			var info = _ordersInfo.TryGetValue(order);
			return info != null && info.IsOwn;
		}

		private void ProcessRisk(Message message)
		{
			foreach (var rule in RiskManager.ProcessRules(message))
			{
				this.AddWarningLog(LocalizedStrings.Str855Params,
					rule.GetType().GetDisplayName(), rule.Title, rule.Action);

				switch (rule.Action)
				{
					case RiskActions.ClosePositions:
						this.ClosePosition();
						break;
					case RiskActions.StopTrading:
						Stop();
						break;
					case RiskActions.CancelOrders:
						CancelActiveOrders();
						break;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}
		}

		private ISecurityProvider SecurityProvider => SafeGetConnector();

		int ISecurityProvider.Count => SecurityProvider.Count;

		event Action<IEnumerable<Security>> ISecurityProvider.Added
		{
			add => SecurityProvider.Added += value;
			remove => SecurityProvider.Added -= value;
		}

		event Action<IEnumerable<Security>> ISecurityProvider.Removed
		{
			add => SecurityProvider.Removed += value;
			remove => SecurityProvider.Removed -= value;
		}

		event Action ISecurityProvider.Cleared
		{
			add => SecurityProvider.Cleared += value;
			remove => SecurityProvider.Cleared -= value;
		}

		/// <inheritdoc />
		public IEnumerable<Security> Lookup(SecurityLookupMessage criteria) => SecurityProvider.Lookup(criteria);

		/// <summary>
		/// New <see cref="StrategyStateMessage"/> occurred event.
		/// </summary>
		public event Action<StrategyStateMessage> NewStateMessage;

		private void RaiseNewStateMessage<T>(string paramName, T value)
		{
			NewStateMessage?.Invoke(new StrategyStateMessage
			{
				StrategyId = Id,
				Statistics =
				{
					{ paramName, Tuple.Create(typeof(T).FullName, value?.ToString()) }
				}
			});
		}

		/// <summary>
		/// Convert to <see cref="StrategyInfoMessage"/>.
		/// </summary>
		/// <param name="transactionId">ID of the original message <see cref="StrategyLookupMessage.TransactionId"/> for which this message is a response.</param>
		/// <returns>The message contains information about strategy.</returns>
		public virtual StrategyInfoMessage ToInfoMessage(long transactionId = 0)
		{
			var msg = new StrategyInfoMessage
			{
				StrategyId = Id,
				StrategyName = Name,
				OriginalTransactionId = transactionId,
			};

			foreach (var parameter in Parameters)
			{
				msg.Parameters.Add(parameter.Key, Tuple.Create(parameter.Value.Value.GetType().FullName, parameter.Value.Value?.ToString()));
			}

			return msg;
		}

		/// <summary>
		/// Apply changes.
		/// </summary>
		/// <param name="message">The message contains information about strategy.</param>
		public virtual void ApplyChanges(StrategyInfoMessage message)
		{
			if (message == null)
				throw new ArgumentNullException(nameof(message));

			foreach (var parameter in message.Parameters)
			{
				if (!Parameters.TryGetValue(parameter.Key, out var param))
				{
					this.AddWarningLog("Unknown parameter '{0}'.", parameter.Key);
					continue;
				}

				if (parameter.Value.Item2 == null)
					param.Value = null;
				else
				{
					param.Value = parameter.Value.Item1 == typeof(Unit).FullName
						? parameter.Value.Item2.ToUnit()
						: parameter.Value.Item2.To(parameter.Value.Item1.To<Type>());
				}
			}
		}

		/// <summary>
		/// Apply incoming command.
		/// </summary>
		/// <param name="stateMsg">The message contains information about strategy state or command to change state.</param>
		public virtual void ApplyCommand(StrategyStateMessage stateMsg)
		{
			if (stateMsg == null)
				throw new ArgumentNullException(nameof(stateMsg));

			switch (stateMsg.Command)
			{
				case StrategyCommands.Start:
				{
					Start();
					break;
				}

				case StrategyCommands.Stop:
				{
					Stop();
					break;
				}

				case StrategyCommands.CancelOrders:
				{
					CancelActiveOrders();
					break;
				}

				case StrategyCommands.RegisterOrder:
				{
					var secId = stateMsg.Statistics.TryGetValue(nameof(Order.Security))?.Item2;
					var pfName = stateMsg.Statistics.TryGetValue(nameof(Order.Portfolio))?.Item2;
					var side = stateMsg.Statistics[nameof(Order.Direction)].Item2.To<Sides>();
					var volume = stateMsg.Statistics[nameof(Order.Volume)].Item2.To<decimal>();
					var price = stateMsg.Statistics.TryGetValue(nameof(Order.Price))?.Item2.To<decimal?>() ?? 0;
					var comment = stateMsg.Statistics.TryGetValue(nameof(Order.Comment))?.Item2;
					var clientCode = stateMsg.Statistics.TryGetValue(nameof(Order.ClientCode))?.Item2;
					var tif = stateMsg.Statistics.TryGetValue(nameof(Order.TimeInForce))?.Item2.To<TimeInForce?>();

					var order = new Order
					{
						Security = secId == null ? Security : this.LookupById(secId),
						Portfolio = pfName == null ? Portfolio : Connector.LookupByPortfolioName(pfName),
						Direction = side,
						Volume = volume,
						Price = price,
						Comment = comment,
						ClientCode = clientCode,
						TimeInForce = tif,
					};

					RegisterOrder(order);
					
					break;
				}

				case StrategyCommands.CancelOrder:
				{
					var orderId = stateMsg.Statistics[nameof(Order.Id)].Item2.To<long>();

					// TODO
#pragma warning disable 618
					CancelOrder(Orders.First(o => o.Id == orderId));
#pragma warning restore 618

					break;
				}

				case StrategyCommands.ClosePosition:
				{
					var slippage = stateMsg.Statistics.TryGetValue(nameof(Order.Slippage))?.Item2.To<decimal?>();
					
					this.ClosePosition(slippage ?? 0);
					
					break;
				}
			}
		}

		//private bool IsChildOrder(Order order)
		//{
		//	var info = _ordersInfo.TryGetValue(order);
		//	return info != null && !info.IsOwn;
		//}

		/// <summary>
		/// Release resources.
		/// </summary>
		protected override void DisposeManaged()
		{
			//_disposeLock.WriteAsync(() =>
			//{
			ChildStrategies.ForEach(s => s.Dispose());
			ChildStrategies.Clear();

			Connector = null;
			//});

			base.DisposeManaged();

			_strategyStat.Remove(this);
		}
	}
}