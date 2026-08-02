using Xunit;

namespace Homelab.Infrastructure.Tests;

// Console.SetOut is process-global, so two test classes capturing stdout at the same time
// clobber each other's buffer. xunit parallelises across classes by default, which made
// DescribeOnlyTests and RetiredTests fail intermittently when run together while both
// passed in isolation — a false failure that looks exactly like a real regression.
//
// Every class that captures Console output must join this collection.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsoleCaptureCollection
{
    public const string Name = "console-capture";
}
