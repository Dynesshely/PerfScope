using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace PerfScope.TestRunner;

/// <summary>
/// 极简 xunit 反射运行器。
/// 背景：本机的 VSTest testhost 启动时需要打开父进程句柄，在受限环境下会抛 Win32Exception(5)，
/// 导致 dotnet test 无法工作。这里直接加载测试程序集、按特性反射执行，行为与 xunit 足够接近：
/// 支持 [Fact]、[Theory]+[InlineData]、[Fact(Skip=...)]、IDisposable、Task / ValueTask 返回值。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        var filter = args.Length > 0 ? args[0] : null;
        Assembly assembly;
        try
        {
            assembly = Assembly.Load("PerfScope.Tests");
        }
        catch (Exception ex)
        {
            Console.WriteLine("无法加载 PerfScope.Tests：" + ex.Message);
            return 2;
        }

        var passed = 0;
        var failed = 0;
        var skipped = 0;
        var unsupported = 0;
        var failures = new List<string>();
        var stopwatch = Stopwatch.StartNew();

        var types = assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(IsTest))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(IsTest)
                .OrderBy(m => m.Name, StringComparer.Ordinal);

            foreach (var method in methods)
            {
                var name = type.Name + "." + method.Name;
                if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var skipReason = GetNamedString(method, "Skip");
                if (skipReason is not null)
                {
                    skipped++;
                    Console.WriteLine("  跳过 " + name + "（" + skipReason + "）");
                    continue;
                }

                if (method.GetCustomAttributesData().Any(a => a.AttributeType.Name == "MemberDataAttribute"))
                {
                    unsupported++;
                    Console.WriteLine("  未支持 MemberData：" + name);
                    continue;
                }

                var cases = BuildCases(method);
                foreach (var caseArgs in cases)
                {
                    var label = caseArgs.Length == 0
                        ? name
                        : name + "(" + string.Join(", ", caseArgs.Select(Format)) + ")";
                    try
                    {
                        RunCase(type, method, caseArgs);
                        passed++;
                        Console.WriteLine("  通过 " + label);
                    }
                    catch (Exception ex)
                    {
                        var inner = Unwrap(ex);
                        failed++;
                        failures.Add(label + Environment.NewLine + "    " + inner);
                        Console.WriteLine("  失败 " + label);
                        Console.WriteLine("        " + inner.GetType().Name + "：" + inner.Message.Replace("\n", "\n        "));
                    }
                }
            }
        }

        stopwatch.Stop();
        Console.WriteLine();
        Console.WriteLine($"合计：通过 {passed}，失败 {failed}，跳过 {skipped}，未支持 {unsupported}，用时 {stopwatch.Elapsed.TotalSeconds:F1}s");
        if (failed > 0)
        {
            Console.WriteLine();
            Console.WriteLine("===== 失败详情 =====");
            foreach (var failure in failures)
            {
                Console.WriteLine(failure);
                Console.WriteLine();
            }

            return 1;
        }

        return 0;
    }

    private static bool IsTest(MethodInfo method)
    {
        var attributes = method.GetCustomAttributesData();
        return attributes.Any(a => a.AttributeType.Name is "FactAttribute" or "TheoryAttribute");
    }

    private static List<object?[]> BuildCases(MethodInfo method)
    {
        var result = new List<object?[]>();
        var isTheory = method.GetCustomAttributesData().Any(a => a.AttributeType.Name == "TheoryAttribute");
        if (!isTheory)
        {
            result.Add(Array.Empty<object?>());
            return result;
        }

        foreach (var attribute in method.GetCustomAttributesData().Where(a => a.AttributeType.Name == "InlineDataAttribute"))
        {
            if (attribute.ConstructorArguments.Count == 0)
            {
                result.Add(Array.Empty<object?>());
                continue;
            }

            var value = attribute.ConstructorArguments[0].Value;
            if (value is IList<CustomAttributeTypedArgument> typedList)
            {
                result.Add(typedList.Select(static item => item.Value).ToArray());
            }
            else
            {
                result.Add(value as object?[] ?? new[] { value });
            }
        }

        if (result.Count == 0)
        {
            result.Add(Array.Empty<object?>());
        }

        return result;
    }

    private static void RunCase(Type type, MethodInfo method, object?[] args)
    {
        var instance = CreateInstance(type);
        try
        {
            var parameters = method.GetParameters();
            var callArgs = new object?[parameters.Length];
            for (var i = 0; i < parameters.Length; i++)
            {
                var value = i < args.Length ? args[i] : null;
                var target = parameters[i].ParameterType;
                if (value is null && target.IsValueType && Nullable.GetUnderlyingType(target) is null)
                {
                    value = Activator.CreateInstance(target);
                }
                else if (value is not null && !target.IsInstanceOfType(value))
                {
                    value = Convert.ChangeType(value, Nullable.GetUnderlyingType(target) ?? target);
                }

                callArgs[i] = value;
            }

            var result = method.Invoke(method.IsStatic ? null : instance, callArgs);
            switch (result)
            {
                case Task task:
                    if (!task.Wait(TimeSpan.FromSeconds(120)))
                    {
                        throw new TimeoutException("测试超过 120 秒未结束");
                    }

                    break;
                case ValueTask valueTask:
                    valueTask.AsTask().GetAwaiter().GetResult();
                    break;
            }
        }
        finally
        {
            (instance as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// 构造测试类实例。xunit 允许构造函数注入 ITestOutputHelper，这里用一个空实现的动态代理顶上；
    /// 其它无法解析的参数（理论测试的数据注入）一律传默认值。
    /// </summary>
    private static object CreateInstance(Type type)
    {
        var constructor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
        if (constructor is null)
        {
            return Activator.CreateInstance(type)!;
        }

        var arguments = constructor.GetParameters()
            .Select(parameter => ResolveArgument(parameter.ParameterType))
            .ToArray();
        return constructor.Invoke(arguments);
    }

    private static object? ResolveArgument(Type parameterType)
    {
        if (parameterType.FullName == "Xunit.Abstractions.ITestOutputHelper")
        {
            return CreateOutputHelper(parameterType);
        }

        if (parameterType.IsValueType)
        {
            return Activator.CreateInstance(parameterType);
        }

        return null;
    }

    private static object CreateOutputHelper(Type interfaceType)
    {
        var create = typeof(DispatchProxy).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.IsGenericMethodDefinition);
        var generic = create.MakeGenericMethod(interfaceType, typeof(NullOutputHelperProxy));
        return generic.Invoke(null, null)!;
    }

    /// <summary>吞掉 xunit 的测试输出，避免控制台噪音。</summary>
    public class NullOutputHelperProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => null;
    }

    private static string? GetNamedString(MemberInfo member, string name)
    {
        foreach (var attribute in member.GetCustomAttributesData())
        {
            if (attribute.AttributeType.Name is not ("FactAttribute" or "TheoryAttribute"))
            {
                continue;
            }

            foreach (var argument in attribute.NamedArguments)
            {
                if (argument.MemberName == name && argument.TypedValue.Value is string text && text.Length > 0)
                {
                    return text;
                }
            }
        }

        return null;
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException or AggregateException && exception.InnerException is not null)
        {
            exception = exception.InnerException;
        }

        ExceptionDispatchInfo.Capture(exception);
        return exception;
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text + "\"",
        _ => value.ToString() ?? "null",
    };
}
