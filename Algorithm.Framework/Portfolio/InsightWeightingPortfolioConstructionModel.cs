﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using QuantConnect.Algorithm.Framework.Alphas;
using QuantConnect.Data.UniverseSelection;

namespace QuantConnect.Algorithm.Framework.Portfolio
{
    /// <summary>
    /// Provides an implementation of <see cref="IPortfolioConstructionModel"/> that generates percent targets based on the
    /// <see cref="Insight.Weight"/>. The target percent holdings of each Symbol is given by the <see cref="Insight.Weight"/>
    /// from the last active <see cref="Insight"/> for that symbol.
    /// For insights of direction <see cref="InsightDirection.Up"/>, long targets are returned and for insights of direction
    /// <see cref="InsightDirection.Down"/>, short targets are returned.
    /// If the sum of all the last active <see cref="Insight"/> per symbol is bigger than 1, it will factor down each target
    /// percent holdings proportionally so the sum is 1.
    /// It will ignore <see cref="Insight"/> that have no <see cref="Insight.Weight"/> value.
    /// </summary>
    public class InsightWeightingPortfolioConstructionModel : PortfolioConstructionModel
    {
        private DateTime _rebalancingTime;
        private readonly TimeSpan _rebalancingPeriod;
        private List<Symbol> _removedSymbols;
        private readonly InsightCollection _insightCollection = new InsightCollection();
        private DateTime? _nextExpiryTime;

        /// <summary>
        /// Initialize a new instance of <see cref="InsightWeightingPortfolioConstructionModel"/>
        /// </summary>
        /// <param name="resolution">Rebalancing frequency</param>
        public InsightWeightingPortfolioConstructionModel(Resolution resolution = Resolution.Daily)
        {
            _rebalancingPeriod = resolution.ToTimeSpan();
        }

        /// <summary>
        /// Create portfolio targets from the specified insights
        /// </summary>
        /// <param name="algorithm">The algorithm instance</param>
        /// <param name="insights">The insights to create portoflio targets from</param>
        /// <returns>An enumerable of portfolio targets to be sent to the execution model</returns>
        public override IEnumerable<IPortfolioTarget> CreateTargets(QCAlgorithmFramework algorithm, Insight[] insights)
        {
            var targets = new List<IPortfolioTarget>();

            if (algorithm.UtcTime <= _nextExpiryTime &&
                algorithm.UtcTime <= _rebalancingTime &&
                insights.Length == 0 &&
                _removedSymbols == null)
            {
                return targets;
            }

            // Ignore insights that don't have Weight value
            _insightCollection.AddRange(insights.Where(insight => insight.Weight.HasValue));

            // Create flatten target for each security that was removed from the universe
            if (_removedSymbols != null)
            {
                var universeDeselectionTargets = _removedSymbols.Select(symbol => new PortfolioTarget(symbol, 0));
                targets.AddRange(universeDeselectionTargets);
                _removedSymbols = null;
            }

            // Get insight that haven't expired of each symbol that is still in the universe
            var activeInsights = _insightCollection.GetActiveInsights(algorithm.UtcTime);

            // Get the last generated active insight for each symbol
            var lastActiveInsights = (from insight in activeInsights
                                    group insight by insight.Symbol into g
                                    select g.OrderBy(x => x.GeneratedTimeUtc).Last()).ToList();

            // We will adjust weights proportionally in case the sum is > 1 so it sums to 1.
            var weightSums = lastActiveInsights.Sum(insight => insight.Weight.Value);
            var weightFactor = 1.0;
            if (weightSums > 1)
            {
                weightFactor = 1 / weightSums;
            }

            var errorSymbols = new HashSet<Symbol>();

            foreach (var insight in lastActiveInsights)
            {
                var target = PortfolioTarget.Percent(algorithm, insight.Symbol, (int)insight.Direction * insight.Weight.Value * weightFactor);
                if (target != null)
                {
                    targets.Add(target);
                }
                else
                {
                    errorSymbols.Add(insight.Symbol);
                }
            }

            // Get expired insights and create flatten targets for each symbol
            var expiredInsights = _insightCollection.RemoveExpiredInsights(algorithm.UtcTime);

            var expiredTargets = from insight in expiredInsights
                                 group insight.Symbol by insight.Symbol into g
                                 where !_insightCollection.HasActiveInsights(g.Key, algorithm.UtcTime) && !errorSymbols.Contains(g.Key)
                                 select new PortfolioTarget(g.Key, 0);

            targets.AddRange(expiredTargets);

            _nextExpiryTime = _insightCollection.GetNextExpiryTime();
            _rebalancingTime = algorithm.UtcTime.Add(_rebalancingPeriod);

            return targets;
        }

        /// <summary>
        /// Event fired each time the we add/remove securities from the data feed
        /// </summary>
        /// <param name="algorithm">The algorithm instance that experienced the change in securities</param>
        /// <param name="changes">The security additions and removals from the algorithm</param>
        public override void OnSecuritiesChanged(QCAlgorithmFramework algorithm, SecurityChanges changes)
        {
            // Get removed symbol and invalidate them in the insight collection
            _removedSymbols = changes.RemovedSecurities.Select(x => x.Symbol).ToList();
            _insightCollection.Clear(_removedSymbols.ToArray());
        }
    }
}
