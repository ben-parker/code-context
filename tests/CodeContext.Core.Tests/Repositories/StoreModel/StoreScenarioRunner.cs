using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodeContext.Core.Repositories.InMemory;

namespace CodeContext.Core.Tests.Repositories.StoreModel;

/// <summary>
/// Runs a seeded scenario against a fresh store and the <see cref="ReferenceGraphModel"/>: after
/// every operation the result (or exception) and the whole read matrix must be equal.
/// <c>equivalentShard</c> makes the shard a swap installs in place of a live one; it must answer
/// every read and every commit exactly as the shard it replaces (a heap copy, the delegating test
/// shard, and later the mapped shard).
/// </summary>
internal static class StoreScenarioRunner
{
    public static async Task RunAsync(int seed, int operations, Func<IGraphShard, IGraphShard> equivalentShard)
    {
        var database = new InMemoryDatabase();
        var model = new ReferenceGraphModel();
        var generator = new StoreScenarioGenerator(seed);
        var history = new List<string>();

        for (var step = 0; step < operations; step++)
        {
            var operation = generator.Next(model);
            history.Add(operation.Describe());
            var expected = operation.ApplyTo(model);
            var actual = operation.ApplyTo(database, equivalentShard);
            var context = $"seed {seed}, step {step}: {operation.Describe()}\nlast operations:\n  "
                + string.Join("\n  ", history.TakeLast(6));
            Assert.True(expected == actual, $"{context}\nmodel returned {expected}\nstore returned {actual}");
            StoreReadMatrix.AssertEqual(
                StoreReadMatrix.Capture(model, generator.IssuedIdentifiers),
                await StoreReadMatrix.CaptureAsync(database, generator.IssuedIdentifiers),
                context);
        }
    }
}
