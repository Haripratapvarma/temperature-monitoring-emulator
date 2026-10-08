// Minimal, source-compatible stand-in for the subset of xUnit used by Tests/.
// Purpose: run the same test sources where nuget.org is unreachable (dotnet build -p:UseXunitShim=true).
// CI uses the real xUnit packages. Supports [Fact], [Theory]+[InlineData], IAsyncLifetime, IDisposable,
// IAsyncDisposable and the Assert members below.
#if XUNIT_SHIM
using System.Collections;
using System.Diagnostics;
using System.Reflection;

namespace Xunit
{
    [AttributeUsage(AttributeTargets.Method)]
    public class FactAttribute : Attribute
    {
        public string? Skip { get; set; }
        public int Timeout { get; set; }
        public string? DisplayName { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TheoryAttribute : FactAttribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    public sealed class InlineDataAttribute(params object?[] data) : Attribute
    {
        public object?[] Data { get; } = data;
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class TraitAttribute(string name, string value) : Attribute
    {
        public string Name { get; } = name;
        public string Value { get; } = value;
    }

    public interface IAsyncLifetime
    {
        Task InitializeAsync();
        Task DisposeAsync();
    }

    public sealed class XunitException(string message) : Exception(message);

    public static class Assert
    {
        private static string Show(object? o) => o switch
        {
            null => "null",
            string s => $"\"{s}\"",
            IEnumerable e => "[" + string.Join(", ", e.Cast<object?>().Take(20).Select(Show)) + "]",
            _ => o.ToString() ?? "",
        };

        private static bool AreEqual(object? a, object? b)
        {
            if (a is null || b is null) return a is null && b is null;
            if (a is not string && b is not string && a is IEnumerable ea && b is IEnumerable eb)
            {
                var la = ea.Cast<object?>().ToList();
                var lb = eb.Cast<object?>().ToList();
                return la.Count == lb.Count && la.Zip(lb).All(p => AreEqual(p.First, p.Second));
            }
            return a.Equals(b);
        }

        public static void Equal<T>(T expected, T actual)
        {
            if (!AreEqual(expected, actual)) throw new XunitException($"Assert.Equal failed. Expected: {Show(expected)} Actual: {Show(actual)}");
        }

        public static void Equal(double expected, double actual, double tolerance)
        {
            if (double.IsNaN(expected) && double.IsNaN(actual)) return;
            if (!(Math.Abs(expected - actual) <= tolerance))
                throw new XunitException($"Assert.Equal failed. Expected: {expected:R} Actual: {actual:R} (tolerance {tolerance})");
        }

        public static void NotEqual<T>(T expected, T actual)
        {
            if (AreEqual(expected, actual)) throw new XunitException($"Assert.NotEqual failed. Both: {Show(actual)}");
        }

        public static void True(bool condition, string? userMessage = null)
        {
            if (!condition) throw new XunitException("Assert.True failed. " + userMessage);
        }

        public static void False(bool condition, string? userMessage = null)
        {
            if (condition) throw new XunitException("Assert.False failed. " + userMessage);
        }

        public static void Null(object? o)
        {
            if (o is not null) throw new XunitException($"Assert.Null failed. Actual: {Show(o)}");
        }

        public static void NotNull(object? o)
        {
            if (o is null) throw new XunitException("Assert.NotNull failed.");
        }

        public static void Same(object? expected, object? actual)
        {
            if (!ReferenceEquals(expected, actual)) throw new XunitException("Assert.Same failed.");
        }

        public static T IsType<T>(object? o)
        {
            if (o is null || o.GetType() != typeof(T)) throw new XunitException($"Assert.IsType failed. Expected {typeof(T)}, got {o?.GetType()}");
            return (T)o;
        }

        public static void InRange<T>(T actual, T low, T high) where T : IComparable<T>
        {
            if (actual.CompareTo(low) < 0 || actual.CompareTo(high) > 0)
                throw new XunitException($"Assert.InRange failed. {actual} not in [{low}, {high}]");
        }

        public static void Contains(string expectedSubstring, string? actual)
        {
            if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
                throw new XunitException($"Assert.Contains failed. \"{expectedSubstring}\" not in {Show(actual)}");
        }

        public static void Contains<T>(T expected, IEnumerable<T> collection)
        {
            if (!collection.Any(x => AreEqual(x, expected))) throw new XunitException($"Assert.Contains failed. {Show(expected)} not in {Show(collection)}");
        }

        public static void Contains<T>(IEnumerable<T> collection, Predicate<T> filter)
        {
            if (!collection.Any(x => filter(x))) throw new XunitException("Assert.Contains failed. No item matched the filter.");
        }

        public static void DoesNotContain<T>(IEnumerable<T> collection, Predicate<T> filter)
        {
            if (collection.Any(x => filter(x))) throw new XunitException("Assert.DoesNotContain failed. An item matched the filter.");
        }

        public static void DoesNotContain(string unexpected, string? actual)
        {
            if (actual is not null && actual.Contains(unexpected, StringComparison.Ordinal))
                throw new XunitException($"Assert.DoesNotContain failed. \"{unexpected}\" found.");
        }

        public static void Empty(IEnumerable collection)
        {
            if (collection.Cast<object?>().Any()) throw new XunitException($"Assert.Empty failed. {Show(collection)}");
        }

        public static void NotEmpty(IEnumerable collection)
        {
            if (!collection.Cast<object?>().Any()) throw new XunitException("Assert.NotEmpty failed.");
        }

        public static T Single<T>(IEnumerable<T> collection)
        {
            var l = collection.ToList();
            if (l.Count != 1) throw new XunitException($"Assert.Single failed. Count = {l.Count}");
            return l[0];
        }

        public static T Single<T>(IEnumerable<T> collection, Predicate<T> predicate)
        {
            var l = collection.Where(x => predicate(x)).ToList();
            if (l.Count != 1) throw new XunitException($"Assert.Single failed. Matching count = {l.Count}");
            return l[0];
        }

        public static void StartsWith(string expectedStart, string? actual)
        {
            if (actual is null || !actual.StartsWith(expectedStart, StringComparison.Ordinal))
                throw new XunitException($"Assert.StartsWith failed. {Show(actual)} does not start with \"{expectedStart}\"");
        }

        public static void All<T>(IEnumerable<T> collection, Action<T> action)
        {
            foreach (var x in collection) action(x);
        }

        public static T Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (Exception ex) when (ex.GetType() == typeof(T)) { return (T)ex; }
            catch (Exception ex) { throw new XunitException($"Assert.Throws failed. Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
            throw new XunitException($"Assert.Throws failed. Expected {typeof(T).Name}, nothing thrown.");
        }

        public static T Throws<T>(Func<object?> func) where T : Exception => Throws<T>(() => { func(); });

        public static T ThrowsAny<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T ex) { return ex; }
            catch (Exception ex) { throw new XunitException($"Assert.ThrowsAny failed. Expected {typeof(T).Name}, got {ex.GetType().Name}"); }
            throw new XunitException($"Assert.ThrowsAny failed. Expected {typeof(T).Name}, nothing thrown.");
        }

        public static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (Exception ex) when (ex.GetType() == typeof(T)) { return (T)ex; }
            catch (Exception ex) { throw new XunitException($"Assert.ThrowsAsync failed. Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
            throw new XunitException($"Assert.ThrowsAsync failed. Expected {typeof(T).Name}, nothing thrown.");
        }

        public static async Task<T> ThrowsAnyAsync<T>(Func<Task> action) where T : Exception
        {
            try { await action(); }
            catch (T ex) { return ex; }
            catch (Exception ex) { throw new XunitException($"Assert.ThrowsAnyAsync failed. Expected {typeof(T).Name}, got {ex.GetType().Name}: {ex.Message}"); }
            throw new XunitException($"Assert.ThrowsAnyAsync failed. Expected {typeof(T).Name}, nothing thrown.");
        }
    }
}

namespace XunitShim
{
    public static class Runner
    {
        public static async Task<int> Main(string[] args)
        {
            string? filter = args.Length > 0 ? args[0] : null;
            var asm = Assembly.GetExecutingAssembly();
            var cases = new List<(Type Type, MethodInfo Method, object?[]? Data, string Name)>();
            foreach (var type in asm.GetTypes().Where(t => t.IsClass && t.IsPublic && !t.IsAbstract).OrderBy(t => t.FullName))
            {
                foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance).OrderBy(m => m.MetadataToken))
                {
                    var fact = m.GetCustomAttribute<Xunit.FactAttribute>();
                    if (fact is null) continue;
                    if (fact is Xunit.TheoryAttribute)
                    {
                        foreach (var d in m.GetCustomAttributes<Xunit.InlineDataAttribute>())
                            cases.Add((type, m, d.Data, $"{type.Name}.{m.Name}({string.Join(", ", d.Data.Select(x => x?.ToString() ?? "null"))})"));
                    }
                    else
                    {
                        cases.Add((type, m, null, $"{type.Name}.{m.Name}"));
                    }
                }
            }
            if (filter is not null) cases = cases.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

            int passed = 0, failed = 0, skipped = 0;
            var total = Stopwatch.StartNew();
            foreach (var (type, method, data, name) in cases)
            {
                var fact = method.GetCustomAttribute<Xunit.FactAttribute>()!;
                if (fact.Skip is not null)
                {
                    skipped++;
                    Console.WriteLine($"[SKIP] {name}: {fact.Skip}");
                    continue;
                }
                var sw = Stopwatch.StartNew();
                try
                {
                    var instance = Activator.CreateInstance(type)!;
                    try
                    {
                        if (instance is Xunit.IAsyncLifetime life) await life.InitializeAsync();
                        var args2 = data is null ? null : ConvertArgs(method, data);
                        var result = method.Invoke(instance, args2);
                        if (result is Task t)
                        {
                            var limit = fact.Timeout > 0 ? TimeSpan.FromMilliseconds(fact.Timeout) : TimeSpan.FromMinutes(2);
                            await t.WaitAsync(limit);
                        }
                    }
                    finally
                    {
                        if (instance is Xunit.IAsyncLifetime life2) await life2.DisposeAsync();
                        if (instance is IAsyncDisposable ad) await ad.DisposeAsync();
                        else if (instance is IDisposable d) d.Dispose();
                    }
                    passed++;
                    Console.WriteLine($"[PASS] {name} ({sw.ElapsedMilliseconds} ms)");
                }
                catch (Exception ex)
                {
                    failed++;
                    var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
                    Console.WriteLine($"[FAIL] {name} ({sw.ElapsedMilliseconds} ms)\n       {inner.GetType().Name}: {inner.Message}");
                    var frame = inner.StackTrace?.Split('\n').FirstOrDefault(l => l.Contains(".cs:"));
                    if (frame is not null) Console.WriteLine("       " + frame.Trim());
                }
            }
            Console.WriteLine($"\nTotal: {cases.Count}, passed: {passed}, failed: {failed}, skipped: {skipped}, time: {total.Elapsed.TotalSeconds:F1} s");
            return failed == 0 ? 0 : 1;
        }

        private static object?[] ConvertArgs(MethodInfo m, object?[] data)
        {
            var ps = m.GetParameters();
            var result = new object?[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                var v = i < data.Length ? data[i] : null;
                var target = Nullable.GetUnderlyingType(ps[i].ParameterType) ?? ps[i].ParameterType;
                result[i] = v is null || target.IsInstanceOfType(v) ? v
                    : target.IsEnum ? Enum.ToObject(target, v)
                    : Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
            }
            return result;
        }
    }
}
#endif
