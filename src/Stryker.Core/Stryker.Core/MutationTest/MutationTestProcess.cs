﻿using Microsoft.Extensions.Logging;
using Stryker.Core.Compiling;
using Stryker.Core.Exceptions;
using Stryker.Core.Logging;
using Stryker.Core.MutantFilters;
using Stryker.Core.Mutants;
using Stryker.Core.Options;
using Stryker.Core.Reporters;
using Stryker.Core.TestRunners;
using Stryker.Core.ToolHelpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;

namespace Stryker.Core.MutationTest
{
    public interface IMutationTestProcessProvider
    {
        IMutationTestProcess Provide(MutationTestInput mutationTestInput, IReporter reporter, IMutationTestExecutor mutationTestExecutor, IStrykerOptions options);
    }

    public class MutationTestProcessProvider : IMutationTestProcessProvider
    {
        public IMutationTestProcess Provide(MutationTestInput mutationTestInput,
            IReporter reporter,
            IMutationTestExecutor mutationTestExecutor,
            IStrykerOptions options)
        {
            return new MutationTestProcess(mutationTestInput, reporter, mutationTestExecutor, options: options);
        }
    }

    public interface IMutationTestProcess
    {
        MutationTestInput Input { get; }
        void Mutate();
        StrykerRunResult Test(IEnumerable<Mutant> mutantsToTest);
        void GetCoverage();
    }

    public class MutationTestProcess : IMutationTestProcess
    {
        public MutationTestInput Input { get; }
        private readonly ICompilingProcess _compilingProcess;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger _logger;
        private readonly IMutantFilter _mutantFilter;
        private readonly IMutationTestExecutor _mutationTestExecutor;
        private readonly IMutantOrchestrator _orchestrator;
        private readonly IReporter _reporter;
        private readonly IStrykerOptions _options;

        public MutationTestProcess(MutationTestInput mutationTestInput,
            IReporter reporter,
            IMutationTestExecutor mutationTestExecutor,
            IMutantOrchestrator orchestrator = null,
            ICompilingProcess compilingProcess = null,
            IFileSystem fileSystem = null,
            IStrykerOptions options = null,
            IMutantFilter mutantFilter = null)
        {
            Input = mutationTestInput;
            _reporter = reporter;
            _options = options;
            _mutationTestExecutor = mutationTestExecutor;
            _orchestrator = orchestrator ?? new MutantOrchestrator(options: _options);
            _compilingProcess = compilingProcess ?? new CompilingProcess(mutationTestInput, new RollbackProcess());
            _fileSystem = fileSystem ?? new FileSystem();
            _logger = ApplicationLogging.LoggerFactory.CreateLogger<MutationTestProcess>();
            _mutantFilter = mutantFilter ?? MutantFilterFactory.Create(options);
        }

        public void Mutate()
        {
            // Mutate source files
            foreach (var file in Input.ProjectInfo.ProjectContents.GetAllFiles())
            {
                _logger.LogDebug($"Mutating {file.Name}");
                // Mutate the syntax tree
                var mutatedSyntaxTree = _orchestrator.Mutate(file.SyntaxTree.GetRoot());
                // Add the mutated syntax tree for compilation
                file.MutatedSyntaxTree = mutatedSyntaxTree.SyntaxTree;
                if (_options.DevMode)
                {
                    _logger.LogTrace($"Mutated {file.Name}:{Environment.NewLine}{mutatedSyntaxTree.ToFullString()}");
                }
                // Filter the mutants
                var allMutants = _orchestrator.GetLatestMutantBatch();

                _mutantFilter.FilterMutants(allMutants, file, _options);

                file.Mutants = allMutants;
            }

            _logger.LogDebug("{0} mutants created", Input.ProjectInfo.ProjectContents.Mutants.Count());

            CompileMutations();

            var skippedMutantGroups = Input.ProjectInfo.ProjectContents.GetAllFiles()
                .SelectMany(f => f.Mutants)
                .Where(x => x.ResultStatus != MutantStatus.NotRun).GroupBy(x => x.ResultStatusReason)
                .OrderBy(x => x.Key);

            foreach (var skippedMutantGroup in skippedMutantGroups)
            {
                _logger.LogInformation("{0} mutants got status {1}. Reason: {2}", skippedMutantGroup.Count(),
                    skippedMutantGroup.First().ResultStatus, skippedMutantGroup.Key);
            }
        }

        private void CompileMutations()
        {
            using var ms = new MemoryStream();
            using var msForSymbols = _options.DevMode ? new MemoryStream() : null;
            // compile the mutated syntax trees
            var compileResult = _compilingProcess.Compile(Input.ProjectInfo.ProjectContents.CompilationSyntaxTrees, ms, msForSymbols, _options.DevMode);

            foreach (var testProject in Input.ProjectInfo.TestProjectAnalyzerResults)
            {
                var injectionPath = Input.ProjectInfo.GetInjectionPath(testProject);
                if (!_fileSystem.Directory.Exists(Path.GetDirectoryName(injectionPath)))
                {
                    _fileSystem.Directory.CreateDirectory(injectionPath);
                }

                // inject the mutated Assembly into the test project
                using var fs = _fileSystem.File.Create(Path.Combine(injectionPath, injectionPath));
                ms.Position = 0;
                ms.CopyTo(fs);

                if (msForSymbols != null)
                {
                    // inject the debug symbols into the test project
                    using var symbolDestination = _fileSystem.File.Create(Path.Combine(
                        Path.GetDirectoryName(injectionPath),
                        Input.ProjectInfo.ProjectUnderTestAnalyzerResult.GetSymbolFileName()));
                    msForSymbols.Position = 0;
                    msForSymbols.CopyTo(symbolDestination);
                }

                _logger.LogDebug("Injected the mutated assembly file into {0}", injectionPath);
            }

            // if a rollback took place, mark the rolled back mutants as status:BuildError
            if (compileResult.RollbackResult?.RollbackedIds.Any() ?? false)
            {
                foreach (var mutant in Input.ProjectInfo.ProjectContents.Mutants
                    .Where(x => compileResult.RollbackResult.RollbackedIds.Contains(x.Id)))
                {
                    // Ignore compilation errors if the mutation is skipped anyways.
                    if (mutant.ResultStatus == MutantStatus.Ignored)
                    {
                        continue;
                    }

                    mutant.ResultStatus = MutantStatus.CompileError;
                    mutant.ResultStatusReason = "Could not compile";
                }
            }

            _logger.LogInformation("{0} mutants detected in {1}", Input.ProjectInfo.ProjectContents.TotalMutants.Count(), Input.ProjectInfo.ProjectContents.Name);
        }

        public StrykerRunResult Test(IEnumerable<Mutant> mutantsToTest)
        {
            if (!mutantsToTest.Any())
            {
                return new StrykerRunResult(_options, double.NaN);
            }
            var mutantGroups = BuildMutantGroupsForTest(mutantsToTest.ToList());

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = _options.ConcurrentTestrunners };
            Parallel.ForEach(mutantGroups, parallelOptions, mutants =>
            {
                var testMutants = new HashSet<Mutant>();

                TestUpdateHandler testUpdateHandler = (testedMutants, failedTests, ranTests, timedOutTest) =>
                {
                    var mustGoOn = !_options.Optimizations.HasFlag(OptimizationFlags.AbortTestOnKill);
                    foreach (var mutant in testedMutants)
                    {
                        mutant.AnalyzeTestRun(failedTests, ranTests, timedOutTest);
                        if (mutant.ResultStatus == MutantStatus.NotRun)
                        {
                            mustGoOn = true;
                        }
                        else if (!testMutants.Contains(mutant))
                        {
                            testMutants.Add(mutant);
                            _reporter.OnMutantTested(mutant);
                        }
                    }

                    return mustGoOn;
                };
                _mutationTestExecutor.Test(mutants, Input.TimeoutMs, testUpdateHandler);
                    
                foreach (var mutant in mutants)
                {
                    if (mutant.ResultStatus == MutantStatus.NotRun)
                    {
                        _logger.LogWarning($"Mutation {mutant.Id} was not fully tested.");
                    }
                    else if (!testMutants.Contains(mutant))
                    {
                        _reporter.OnMutantTested(mutant);
                    }
                }
            });

            _mutationTestExecutor.TestRunner.Dispose();

            return new StrykerRunResult(_options, Input.ProjectInfo.ProjectContents.GetMutationScore());
        }

        private IEnumerable<List<Mutant>> BuildMutantGroupsForTest(IReadOnlyCollection<Mutant> mutantsNotRun)
        {

            if (_options.Optimizations.HasFlag(OptimizationFlags.DisableTestMix) || !_options.Optimizations.HasFlag(OptimizationFlags.CoverageBasedTest))
            {
                return mutantsNotRun.Select(x => new List<Mutant> { x });
            }

            var blocks = new List<List<Mutant>>(mutantsNotRun.Count);
            var mutantsToGroup = mutantsNotRun.ToList();
            // we deal with mutants needing full testing first
            blocks.AddRange(mutantsToGroup.Where(m => m.MustRunAgainstAllTests).Select(m => new List<Mutant> { m }));
            mutantsToGroup.RemoveAll(m => m.MustRunAgainstAllTests);
            var testsCount = mutantsToGroup.SelectMany(m => m.CoveringTests.GetList()).Distinct().Count();
            mutantsToGroup = mutantsToGroup.OrderByDescending(m => m.CoveringTests.Count).ToList();
            for (var i = 0; i < mutantsToGroup.Count; i++)
            {
                var usedTests = mutantsToGroup[i].CoveringTests.GetList().ToList();
                var nextBlock = new List<Mutant> { mutantsToGroup[i] };
                for (var j = i + 1; j < mutantsToGroup.Count; j++)
                {
                    if (mutantsToGroup[j].CoveringTests.Count + usedTests.Count > testsCount ||
                        mutantsToGroup[j].CoveringTests.ContainsAny(usedTests))
                    {
                        continue;
                    }

                    nextBlock.Add(mutantsToGroup[j]);
                    usedTests.AddRange(mutantsToGroup[j].CoveringTests.GetList());
                    mutantsToGroup.RemoveAt(j--);
                }

                blocks.Add(nextBlock);
            }

            _logger.LogDebug($"Mutations will be tested in {blocks.Count} test runs, instead of {mutantsNotRun.Count}.");
            return blocks;
        }

        public void GetCoverage()
        {
            var (targetFrameworkDoesNotSupportAppDomain, targetFrameworkDoesNotSupportPipe) = Input.ProjectInfo.ProjectUnderTestAnalyzerResult.CompatibilityModes();
            var mutantsToScan = Input.ProjectInfo.ProjectContents.Mutants.Where(x => x.ResultStatus == MutantStatus.NotRun).ToList();
            foreach (var mutant in mutantsToScan)
            {
                mutant.CoveringTests = new TestListDescription(null);
            }
            var testResult = _mutationTestExecutor.TestRunner.CaptureCoverage(mutantsToScan, targetFrameworkDoesNotSupportPipe, targetFrameworkDoesNotSupportAppDomain);
            if (testResult.FailingTests.Count == 0)
            {
                // force static mutants to be tested against all tests.
                if (!_options.Optimizations.HasFlag(OptimizationFlags.CaptureCoveragePerTest))
                {
                    foreach (var mutant in mutantsToScan.Where(mutant => mutant.IsStaticValue))
                    {
                        mutant.MustRunAgainstAllTests = true;
                    }
                }
                foreach (var mutant in mutantsToScan)
                {
                    if (!mutant.MustRunAgainstAllTests && mutant.CoveringTests.IsEmpty)
                    {
                        mutant.ResultStatus = MutantStatus.NoCoverage;
                    }
                    else if (!_options.Optimizations.HasFlag(OptimizationFlags.CoverageBasedTest))
                    {
                        mutant.CoveringTests = TestListDescription.EveryTest();
                    }
                }

                return;
            }
            _logger.LogWarning("Test run with no active mutation failed. Stryker failed to correctly generate the mutated assembly. Please report this issue on github with a logfile of this run.");
            throw new StrykerInputException("No active mutant testrun was not successful.", testResult.ResultMessage);
        }
    }
}