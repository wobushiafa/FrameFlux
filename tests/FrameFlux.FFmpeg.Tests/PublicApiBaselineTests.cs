using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using FrameFlux.FFmpeg;
using Xunit;

namespace FrameFlux.FFmpeg.Tests;

public sealed class PublicApiBaselineTests
{
    private static readonly NullabilityInfoContext Nullability = new();

    [Fact]
    public void MainAssemblies_MatchReviewedPublicApiBaseline()
    {
        var actual = CreateBaseline(
            typeof(IMediaPlayer).Assembly,
            typeof(FfmpegMediaPlayer).Assembly);
        var baselinePath = Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.txt");
        var expected = File.ReadAllText(baselinePath).ReplaceLineEndings("\n").TrimEnd();

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            var actualPath = Path.Combine(AppContext.BaseDirectory, "PublicApiBaseline.actual.txt");
            File.WriteAllText(actualPath, actual + Environment.NewLine);
            Assert.Fail(
                $"The public API changed. Review '{actualPath}' and update " +
                "PublicApiBaseline.txt only when the contract change is intentional.");
        }
    }

    private static string CreateBaseline(params Assembly[] assemblies)
    {
        var builder = new StringBuilder();
        foreach (var assembly in assemblies.OrderBy(item => item.GetName().Name, StringComparer.Ordinal))
        {
            builder.Append('[').Append(assembly.GetName().Name).AppendLine("]");
            foreach (var type in assembly.GetExportedTypes().OrderBy(item => item.FullName, StringComparer.Ordinal))
            {
                AppendType(builder, type);
            }
        }

        return builder.ToString().ReplaceLineEndings("\n").TrimEnd();
    }

    private static void AppendType(StringBuilder builder, Type type)
    {
        if (type.IsEnum)
        {
            builder.Append("enum ").Append(FormatType(type))
                .Append(" : ").AppendLine(FormatType(Enum.GetUnderlyingType(type)));
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                builder.Append("  ").Append(field.Name).Append(" = ")
                    .AppendLine(FormatDefaultValue(field.GetRawConstantValue()));
            }

            return;
        }

        builder.Append(GetTypeKind(type)).Append(' ').Append(FormatType(type));
        var inheritance = GetInheritance(type).ToArray();
        if (inheritance.Length > 0)
        {
            builder.Append(" : ").Append(string.Join(", ", inheritance));
        }

        builder.AppendLine();
        const BindingFlags declaredPublic = BindingFlags.Public | BindingFlags.Instance |
            BindingFlags.Static | BindingFlags.DeclaredOnly;

        foreach (var constructor in type.GetConstructors(declaredPublic).OrderBy(FormatMember, StringComparer.Ordinal))
        {
            builder.Append("  ").AppendLine(FormatMember(constructor));
        }

        foreach (var field in type.GetFields(declaredPublic).OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append("  field ").Append(FormatType(field.FieldType, Nullability.Create(field))).Append(' ')
                .Append(field.Name);
            if (field.IsLiteral)
            {
                builder.Append(" = ").Append(FormatDefaultValue(field.GetRawConstantValue()));
            }

            builder.AppendLine();
        }

        foreach (var property in type.GetProperties(declaredPublic).OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append("  property ").Append(FormatType(property.PropertyType, Nullability.Create(property))).Append(' ')
                .Append(property.Name).Append(" { ");
            if (property.GetMethod?.IsPublic == true)
            {
                builder.Append("get; ");
            }

            if (property.SetMethod?.IsPublic == true)
            {
                var setterKind = property.SetMethod.ReturnParameter.GetRequiredCustomModifiers()
                    .Contains(typeof(IsExternalInit)) ? "init; " : "set; ";
                builder.Append(setterKind);
            }

            builder.AppendLine("}");
        }

        foreach (var eventInfo in type.GetEvents(declaredPublic).OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append("  event ").Append(FormatType(eventInfo.EventHandlerType!, Nullability.Create(eventInfo))).Append(' ')
                .AppendLine(eventInfo.Name);
        }

        foreach (var method in type.GetMethods(declaredPublic)
                     .Where(method => !method.IsSpecialName && !IsGeneratedRecordMethod(method))
                     .OrderBy(FormatMember, StringComparer.Ordinal))
        {
            builder.Append("  ").AppendLine(FormatMember(method));
        }
    }

    private static IEnumerable<string> GetInheritance(Type type)
    {
        if (type.BaseType is not null && type.BaseType != typeof(object) && type.BaseType != typeof(ValueType))
        {
            yield return FormatType(type.BaseType);
        }

        foreach (var interfaceType in type.GetInterfaces()
                     .OrderBy(interfaceType => FormatType(interfaceType), StringComparer.Ordinal))
        {
            yield return FormatType(interfaceType);
        }
    }

    private static string GetTypeKind(Type type) =>
        type.IsInterface ? "interface" : type.IsValueType ? "struct" : "class";

    private static bool IsGeneratedRecordMethod(MethodInfo method) =>
        method.Name is "Deconstruct" or "Equals" or "GetHashCode" or "ToString" ||
        method.Name.Contains('<', StringComparison.Ordinal);

    private static string FormatMember(MethodBase method)
    {
        var builder = new StringBuilder();
        if (method is MethodInfo methodInfo)
        {
            builder.Append("method ").Append(FormatType(
                    methodInfo.ReturnType,
                    Nullability.Create(methodInfo.ReturnParameter))).Append(' ')
                .Append(methodInfo.Name);
            if (methodInfo.IsGenericMethodDefinition)
            {
                builder.Append('<').Append(string.Join(", ", methodInfo.GetGenericArguments().Select(arg => arg.Name)))
                    .Append('>');
            }
        }
        else
        {
            builder.Append("constructor ").Append(method.DeclaringType?.Name.Split('`')[0]);
        }

        builder.Append('(').Append(string.Join(", ", method.GetParameters().Select(FormatParameter)))
            .Append(')');
        return builder.ToString();
    }

    private static string FormatParameter(ParameterInfo parameter)
    {
        var builder = new StringBuilder();
        if (parameter.GetCustomAttribute<ParamArrayAttribute>() is not null)
        {
            builder.Append("params ");
        }
        else if (parameter.IsOut)
        {
            builder.Append("out ");
        }
        else if (parameter.ParameterType.IsByRef)
        {
            builder.Append(parameter.IsIn ? "in " : "ref ");
        }

        builder.Append(FormatType(parameter.ParameterType, Nullability.Create(parameter)))
            .Append(' ').Append(parameter.Name);
        if (parameter.HasDefaultValue)
        {
            var parameterType = parameter.ParameterType.IsByRef
                ? parameter.ParameterType.GetElementType()!
                : parameter.ParameterType;
            var defaultValue = parameter.DefaultValue is null &&
                parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null
                    ? "default"
                    : FormatDefaultValue(parameter.DefaultValue);
            builder.Append(" = ").Append(defaultValue);
        }

        return builder.ToString();
    }

    private static string FormatType(Type type, NullabilityInfo? nullability = null)
    {
        if (type.IsByRef)
        {
            return FormatType(type.GetElementType()!, nullability);
        }

        if (type.IsArray)
        {
            var arrayName = FormatType(type.GetElementType()!, nullability?.ElementType) + "[]";
            return IsNullableReference(type, nullability) ? arrayName + "?" : arrayName;
        }

        if (type.IsGenericParameter)
        {
            return IsNullableReference(type, nullability) ? type.Name + "?" : type.Name;
        }

        if (type.IsGenericType)
        {
            var definition = type.GetGenericTypeDefinition();
            if (definition == typeof(Nullable<>))
            {
                return FormatType(type.GetGenericArguments()[0], nullability?.GenericTypeArguments.FirstOrDefault()) + "?";
            }

            var name = (definition.FullName ?? definition.Name).Split('`')[0].Replace('+', '.');
            var genericArguments = type.GetGenericArguments();
            var formattedArguments = genericArguments.Select((argument, index) =>
                FormatType(argument, nullability?.GenericTypeArguments.ElementAtOrDefault(index)));
            var genericName = name + "<" + string.Join(", ", formattedArguments) + ">";
            return IsNullableReference(type, nullability) ? genericName + "?" : genericName;
        }

        var typeName = type.FullName?.Replace('+', '.') ?? type.Name;
        return IsNullableReference(type, nullability) ? typeName + "?" : typeName;
    }

    private static bool IsNullableReference(Type type, NullabilityInfo? nullability) =>
        !type.IsValueType && nullability?.ReadState == NullabilityState.Nullable;

    private static string FormatDefaultValue(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        char character => "'" + character + "'",
        bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "null"
    };
}
