using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Bencodex.Types;
using CommandLine;
using Libplanet.Action;
using Libplanet.Blockchain;
using Libplanet.Blockchain.Policies;
using Libplanet.Blocks;
using Libplanet.Crypto;
using Libplanet.Explorer.Executable.Exceptions;
using Libplanet.Explorer.Interfaces;
using Libplanet.Explorer.Store;
using Libplanet.Net;
using Libplanet.Store;
using Libplanet.Tx;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Hosting;
using NetMQ;
using Serilog;
using Serilog.Events;

namespace Libplanet.Explorer.Executable
{
    /// <summary>
    /// The program entry point to run a web server.
    /// </summary>
    public class Program
    {
        public static async Task Main(string[] args)
        {
            Options options = Options.Parse(args, Console.Error);

            var loggerConfig = new LoggerConfiguration();
            loggerConfig = options.Debug
                ? loggerConfig.MinimumLevel.Debug()
                : loggerConfig.MinimumLevel.Information();
            loggerConfig = loggerConfig
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .Enrich.FromLogContext()
                .WriteTo.Console();
            Log.Logger = loggerConfig.CreateLogger();

            try
            {
                IRichStore store = LoadStore(options);

                var pendingTxs = store.IterateStagedTransactionIds()
                    .ToImmutableHashSet();
                store.UnstageTransactionIds(pendingTxs);
                Log.Debug("Pending txs unstaged. [{PendingCount}]", pendingTxs.Count);

                IBlockPolicy<AppAgnosticAction> policy = new DumbBlockPolicy(
                    new BlockPolicy<AppAgnosticAction>(
                    null,
                    blockIntervalMilliseconds: options.BlockIntervalMilliseconds,
                    minimumDifficulty: options.MinimumDifficulty,
                    difficultyBoundDivisor: options.DifficultyBoundDivisor,
                    maxGenesisBytes: 1024 * 1024 * 15)
                );
                var blockChain =
                    new BlockChain<AppAgnosticAction>(policy, store, store, options.GenesisBlock);
                Startup.PreloadedSingleton = false;
                Startup.BlockChainSingleton = blockChain;
                Startup.StoreSingleton = store;

                IWebHost webHost = WebHost.CreateDefaultBuilder()
                    .UseStartup<ExplorerStartup<AppAgnosticAction, Startup>>()
                    .UseSerilog()
                    .UseUrls($"http://{options.Host}:{options.Port}/")
                    .Build();

                Swarm<AppAgnosticAction> swarm = null;
                if (options.Seeds.Any())
                {
                    string aggregatedSeedStrings =
                        options.SeedStrings.Aggregate(string.Empty, (s, s1) => s + s1);
                    Console.Error.WriteLine(
                        $"Seeds are {aggregatedSeedStrings}");

                    // TODO: Take privateKey as a CLI option
                    // TODO: Take appProtocolVersion as a CLI option
                    // TODO: Take host as a CLI option
                    // TODO: Take listenPort as a CLI option
                    if (options.IceServer is null)
                    {
                        Console.Error.WriteLine(
                            "error: -s/--seed option requires -I/--ice-server as well."
                        );
                        Environment.Exit(1);
                        return;
                    }

                    Console.Error.WriteLine("Creating Swarm.");

                    var privateKey = new PrivateKey();

                    // FIXME: The appProtocolVersion should be fixed properly.
                    var swarmOptions = new SwarmOptions
                    {
                        MaxTimeout = TimeSpan.FromSeconds(10),
                    };

                    swarm = new Swarm<AppAgnosticAction>(
                        blockChain,
                        privateKey,
                        options.AppProtocolVersionToken is string t
                            ? AppProtocolVersion.FromToken(t)
                            : default(AppProtocolVersion),
                        differentAppProtocolVersionEncountered: (p, pv, lv) => true,
                        workers: options.Workers,
                        iceServers: new[] { options.IceServer },
                        options: swarmOptions
                    );
                }

                using (var cts = new CancellationTokenSource())
                using (swarm)
                {
                    Console.CancelKeyPress += (sender, eventArgs) =>
                    {
                        eventArgs.Cancel = true;
                        cts.Cancel();
                    };

                    try
                    {
                        await Task.WhenAll(
                            webHost.RunAsync(cts.Token),
                            StartSwarmAsync(swarm, options.Seeds, cts.Token)
                        );
                    }
                    catch (OperationCanceledException)
                    {
                        await swarm?.StopAsync(waitFor: TimeSpan.FromSeconds(1))
                            .ContinueWith(_ => NetMQConfig.Cleanup(false));
                    }
                }
            }
            catch (InvalidOptionValueException e)
            {
                string expectedValues = string.Join(", ", e.ExpectedValues);
                Console.Error.WriteLine($"Unexpected value given through '{e.OptionName}'\n"
                          + $"  given value: {e.OptionValue}\n"
                          + $"  expected values: {expectedValues}");
            }
        }

        private static IRichStore LoadStore(Options options)
        {
            bool readOnlyMode = options.Seeds is null;
            BaseBlockStatesStore innerStore;
            switch (options.StoreType)
            {
                case "rocksdb":
                    innerStore = new RocksDBStore.RocksDBStore(
                      options.StorePath,
                      statesCacheSize: 2,
                      maxTotalWalSize: 16 * 1024 * 1024,
                      keepLogFileNum: 1);
                    break;
                case "default":
                    innerStore = new DefaultStore(
                        options.StorePath,
                        flush: false,
                        readOnly: readOnlyMode);
                    break;
                default:
                    // FIXME: give available store type as argument hint without code duplication.
                    var availableStoreTypes = new[] { "rocksdb", "default" };
                    string longOptionName = options.GetType().GetProperty(nameof(options.StoreType))
                        .GetCustomAttribute<OptionAttribute>().LongName;
                    throw new InvalidOptionValueException(
                        "--" + longOptionName,
                        options.StoreType,
                        availableStoreTypes);
            }

            bool useMySQL = !string.IsNullOrEmpty(options.MySQLDatabase) &&
                            !string.IsNullOrEmpty(options.MySQLPassword) &&
                            !string.IsNullOrEmpty(options.MySQLServer) &&
                            !string.IsNullOrEmpty(options.MySQLUsername) &&
                            !(options.MySQLPort is null);
            if (useMySQL)
            {
                var mySqlOptions = new MySQLRichStoreOptions(
                    options.MySQLDatabase,
                    options.MySQLServer,
                    options.MySQLPort.Value,
                    options.MySQLUsername,
                    options.MySQLPassword);
                return new MySQLRichStore(
                    innerStore,
                    mySqlOptions
                );
            }
            else
            {
                return new LiteDBRichStore(
                    innerStore,
                    path: options.StorePath,
                    flush: false,
                    readOnly: readOnlyMode
                );
            }
        }

        private static async Task StartSwarmAsync(
            Swarm<AppAgnosticAction> swarm,
            IEnumerable<Peer> seeds,
            CancellationToken cancellationToken)
        {
            if (swarm is null)
            {
                Startup.PreloadedSingleton = true;
                return;
            }

            try
            {
                Console.Error.WriteLine("Bootstrapping.");
                await swarm.BootstrapAsync(
                    seeds,
                    5000,
                    5000,
                    cancellationToken: cancellationToken
                );
            }
            catch (TimeoutException)
            {
                Console.Error.WriteLine("No any neighbors.");
            }

            // Since explorer does not require states, turn off trustedPeer option.
            var trustedPeers = ImmutableHashSet<Address>.Empty;
            Console.Error.WriteLine("Starts preloading.");
            await swarm.PreloadAsync(
                dialTimeout: TimeSpan.FromSeconds(15),
                trustedStateValidators: trustedPeers,
                cancellationToken: cancellationToken
            );
            Console.Error.WriteLine("Finished preloading.");
            Startup.PreloadedSingleton = true;

            await swarm.StartAsync(cancellationToken: cancellationToken);
        }

        internal class DumbBlockPolicy : IBlockPolicy<AppAgnosticAction>
        {
            private readonly IBlockPolicy<AppAgnosticAction> _impl;

            public DumbBlockPolicy(BlockPolicy<AppAgnosticAction> blockPolicy)
            {
                _impl = blockPolicy;
            }

            public IAction BlockAction => _impl.BlockAction;

            public int MaxTransactionsPerBlock => _impl.MaxTransactionsPerBlock;

            public bool DoesTransactionFollowsPolicy(
                Transaction<AppAgnosticAction> transaction, BlockChain<AppAgnosticAction> blockChain
            )
            {
                return _impl.DoesTransactionFollowsPolicy(transaction, blockChain);
            }

            public int GetMaxBlockBytes(long index)
            {
                return _impl.GetMaxBlockBytes(index);
            }

            public long GetNextBlockDifficulty(BlockChain<AppAgnosticAction> blocks)
            {
                return 0;
            }

            public InvalidBlockException ValidateNextBlock(
                BlockChain<AppAgnosticAction> blocks, Block<AppAgnosticAction> nextBlock
            )
            {
                return _impl.ValidateNextBlock(blocks, nextBlock);
            }
        }

        internal class AppAgnosticAction : IAction
        {
            public IValue PlainValue
            {
                get;
                private set;
            }

            public void LoadPlainValue(
                IValue plainValue)
            {
                PlainValue = plainValue;
            }

            public IAccountStateDelta Execute(IActionContext context)
            {
                return context.PreviousStates;
            }

            public void Render(
                IActionContext context,
                IAccountStateDelta nextStates)
            {
            }

            public void RenderError(IActionContext context, Exception exception)
            {
            }

            public void Unrender(
                IActionContext context,
                IAccountStateDelta nextStates)
            {
            }

            public void UnrenderError(IActionContext context, Exception exception)
            {
            }
        }

        internal class Startup : IBlockChainContext<AppAgnosticAction>
        {
            public bool Preloaded => PreloadedSingleton;

            public BlockChain<AppAgnosticAction> BlockChain => BlockChainSingleton;

            public IStore Store => StoreSingleton;

            internal static bool PreloadedSingleton { get; set; }

            internal static BlockChain<AppAgnosticAction> BlockChainSingleton { get; set; }

            internal static IStore StoreSingleton { get; set; }
        }
    }
}
