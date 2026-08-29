using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;

namespace LibTmux.Query;

internal sealed record QueryFieldAccessor(
    Func<object, object?> Read,
    Type ValueType);

internal enum QueryFieldRole
{
    Scalar,
    Relation,
}

internal readonly record struct QueryBindingMetrics(
    int FieldBindings,
    int RegexBindings);

internal sealed class QueryPlanBindings
{
    private readonly Dictionary<FieldKey, QueryFieldAccessor> _fields = [];
    private readonly QueryValidationResult _validation;

    internal QueryPlanBindings(QueryValidationResult validation) =>
        _validation = validation;

    internal QueryBindingMetrics Metrics => new(_fields.Count, _validation.RegexCount);

    internal Regex Regex(RegexNode node) => _validation.GetRegex(node);

    internal QueryFieldAccessor Field(
        FieldNode field,
        Type elementType,
        QueryFieldRole role)
    {
        FieldKey key = new(elementType, field.WireName, role);
        if (_fields.TryGetValue(key, out QueryFieldAccessor? accessor))
        {
            return accessor;
        }

        accessor = ResolveField(field, elementType, role);
        _fields.Add(key, accessor);
        return accessor;
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:Members might be removed",
        Justification = "Every public compilation entry point declares the metadata requirement.")]
    private static QueryFieldAccessor ResolveField(
        FieldNode field,
        Type elementType,
        QueryFieldRole role)
    {
        if (!QueryFieldCatalog.TryGetTarget(field.WireName, out _))
        {
            throw Unsupported($"Field '{field.WireName}' is not in the query catalog.");
        }

        QueryFieldAccessor? accessor = role switch
        {
            QueryFieldRole.Scalar when QueryFieldCatalog.TryBindEntityScalar(
                elementType,
                field.WireName,
                out QueryFieldAccessor scalar) => scalar,
            QueryFieldRole.Relation when QueryFieldCatalog.TryBindEntityRelation(
                elementType,
                field.WireName,
                out QueryFieldAccessor relation) => relation,
            _ => null,
        };
        if (accessor is null)
        {
            string property =
                QueryFieldCatalog.TryGetProperty(elementType, field.WireName, out string mapped)
                    ? mapped
                    : ToClrName(field.WireName);
            PropertyInfo? member;
            try
            {
                member = elementType.GetProperty(
                    property,
                    BindingFlags.Instance | BindingFlags.Public);
            }
            catch (AmbiguousMatchException)
            {
                member = null;
            }

            if (member?.GetMethod is not { IsStatic: false, IsPublic: true }
                || member.GetIndexParameters().Length != 0)
            {
                throw Unsupported(
                    $"Type '{elementType.Name}' exposes no readable member for field "
                    + $"'{field.WireName}'.");
            }

            accessor = new QueryFieldAccessor(member.GetValue, member.PropertyType);
        }

        if (role == QueryFieldRole.Scalar)
        {
            RequireScalarType(field, accessor.ValueType);
        }

        return accessor;
    }

    internal static Type RelationElementType(FieldNode field, Type relationType) =>
        SequenceElementType(relationType)
        ?? throw Unsupported($"Field '{field.WireName}' is not a typed relation.");

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070:Interfaces might be removed",
        Justification = "Every public compilation entry point declares the metadata requirement.")]
    private static Type? SequenceElementType(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType();
        }

        IEnumerable<Type> candidates = type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? [type]
                : type.GetInterfaces().Where(static candidate =>
                    candidate.IsGenericType
                    && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        Type[] elements =
        [
            .. candidates.Select(static candidate => candidate.GetGenericArguments()[0])
                .Distinct(),
        ];
        return elements.Length == 1 ? elements[0] : null;
    }

    private static void RequireScalarType(FieldNode field, Type propertyType)
    {
        if (!QueryFieldCatalog.TryGetKind(field.WireName, out QueryValueKind kind))
        {
            throw Unsupported($"Field '{field.WireName}' is not in the query catalog.");
        }

        Type valueType = Nullable.GetUnderlyingType(propertyType) ?? propertyType;
        bool compatible = kind switch
        {
            QueryValueKind.Boolean => valueType == typeof(bool),
            QueryValueKind.Int64 => IsInteger(valueType),
            QueryValueKind.String => valueType == typeof(string),
            QueryValueKind.Instant => IsInteger(valueType),
            QueryValueKind.Enum => valueType == typeof(string) || valueType.IsEnum,
            QueryValueKind.TypedId => IsTypedId(field.Target, valueType),
            _ => false,
        };
        if (!compatible)
        {
            throw Unsupported(
                $"Member for field '{field.WireName}' has incompatible type "
                + $"'{propertyType.Name}'.");
        }
    }

    private static bool IsInteger(Type type) =>
        type == typeof(sbyte)
        || type == typeof(byte)
        || type == typeof(short)
        || type == typeof(ushort)
        || type == typeof(int)
        || type == typeof(uint)
        || type == typeof(long)
        || type == typeof(ulong);

    private static bool IsTypedId(QueryTarget target, Type type) =>
        type == typeof(string)
        || target == QueryTarget.Session && type == typeof(SessionId)
        || target == QueryTarget.Window && type == typeof(WindowId)
        || target == QueryTarget.Pane && type == typeof(PaneId);

    private static string ToClrName(string wireName) =>
        string.Concat(
            wireName.Split('_', StringSplitOptions.RemoveEmptyEntries)
                .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static UnsupportedQueryExpressionException Unsupported(string message) => new(message);

    private readonly record struct FieldKey(
        Type Owner,
        string WireName,
        QueryFieldRole Role);
}
