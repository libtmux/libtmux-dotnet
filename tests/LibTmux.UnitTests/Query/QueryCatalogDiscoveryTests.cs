using System.Text.RegularExpressions;
using LibTmux.Query;
using LibTmux.Query.Json;

namespace LibTmux.UnitTests.Query;

public sealed class QueryCatalogDiscoveryTests
{
    [Fact]
    public void Discovery_preserves_scalar_relation_and_capture_distinctions()
    {
        QueryFieldDescriptor path = Field(QueryTarget.Pane, "pane_current_path");
        Assert.Equal(QueryValueKind.String, path.ValueKind);
        Assert.Equal("CurrentPath", path.ScalarPropertyPath);
        Assert.True(path.IsNullable);
        Assert.Equal(SnapshotDepth.Panes, path.MinimumSnapshotDepth);
        Assert.Null(path.RelationPropertyPath);

        QueryFieldDescriptor panes = Field(QueryTarget.Window, "window_panes");
        Assert.Equal("Panes.Count", panes.ScalarPropertyPath);
        Assert.Equal("Panes", panes.RelationPropertyPath);
        Assert.Equal(QueryValueKind.Int64, panes.ValueKind);
        Assert.False(panes.IsNullable);
        Assert.Equal(QueryTarget.Pane, panes.RelatedTarget);
        Assert.Equal(QueryRelationCardinality.Many, panes.Cardinality);
        Assert.Equal(SnapshotDepth.Panes, panes.MinimumSnapshotDepth);

        QueryFieldDescriptor active = Field(QueryTarget.Session, "session_active_window");
        Assert.Equal("ActiveWindow.Value", active.RelationPropertyPath);
        Assert.Null(active.ScalarPropertyPath);
        Assert.Null(active.ValueKind);
        Assert.Null(active.IsNullable);
        Assert.Equal(QueryRelationCardinality.One, active.Cardinality);
        Assert.Equal(SnapshotDepth.Windows, active.MinimumSnapshotDepth);

        QueryFieldDescriptor unbound = Field(QueryTarget.Client, "client_id");
        Assert.Null(unbound.ScalarPropertyPath);
        Assert.Null(unbound.IsNullable);
        Assert.All(QueryFieldCatalog.GetFields(QueryTarget.Client), field => Assert.Null(field.MinimumSnapshotDepth));
        Assert.Equal(QueryFieldCatalog.WireNames.Order(StringComparer.Ordinal),
            Enum.GetValues<QueryTarget>().SelectMany(QueryFieldCatalog.GetFields)
                .Select(field => field.WireName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Discovery_is_read_only_and_rejects_an_unknown_target()
    {
        IReadOnlyList<QueryFieldDescriptor> fields = QueryFieldCatalog.GetFields(QueryTarget.Pane);
        Assert.Throws<NotSupportedException>(() => ((IList<QueryFieldDescriptor>)fields).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)fields[0].Operators).Clear());
        Assert.Throws<ArgumentOutOfRangeException>(() => QueryFieldCatalog.GetFields((QueryTarget)99));
        Assert.All(fields, field => Assert.Equal(QueryTarget.Pane, field.Target));
    }

    [Fact]
    public void Advertised_operations_match_the_current_wire_validator()
    {
        string[] operations =
        [
            "equal", "notEqual", "lessThan", "lessThanOrEqual", "greaterThan", "greaterThanOrEqual",
            "stringEqualOrdinal", "stringEqualOrdinalIgnoreCase", "startsWithOrdinal", "endsWithOrdinal",
            "containsOrdinal", "regex", "any", "all", "related",
        ];
        foreach (QueryFieldDescriptor field in Enum.GetValues<QueryTarget>().SelectMany(QueryFieldCatalog.GetFields))
        {
            foreach (string operation in operations)
            {
                QueryDocument document = new(QueryDocument.CurrentSchema, QueryDocument.CurrentVersion,
                    field.Target, Predicate(field, operation));
                Exception? failure = Record.Exception(() => Assert.Equal(document,
                    QueryJson.Deserialize(QueryJson.Serialize(document))));
                if (failure is not null)
                {
                    Assert.IsType<UnsupportedQueryExpressionException>(failure);
                }
                Assert.Equal(failure is null, field.Operators.Contains(operation, StringComparer.Ordinal));
            }
        }
    }

    private static QueryFieldDescriptor Field(QueryTarget target, string wireName) =>
        Assert.Single(QueryFieldCatalog.GetFields(target), field => field.WireName == wireName);

    private static QueryNode Predicate(QueryFieldDescriptor descriptor, string operation)
    {
        FieldNode field = new(descriptor.Target, descriptor.WireName);
        ConstantNode truth = new(new BooleanConstant(true));
        if (Enum.TryParse(operation, ignoreCase: true, out QueryComparison comparison))
        {
            QueryConstant constant = descriptor.ValueKind switch
            {
                QueryValueKind.Boolean => new BooleanConstant(true),
                QueryValueKind.Int64 => new Int64Constant(1),
                QueryValueKind.String => new StringConstant("value"),
                QueryValueKind.TypedId => new TypedIdConstant(descriptor.Target, descriptor.Target switch
                {
                    QueryTarget.Session => "$1",
                    QueryTarget.Window => "@1",
                    QueryTarget.Pane => "%1",
                    _ => "client-1",
                }),
                _ => new NullConstant(),
            };
            return new ComparisonNode(comparison, field, new ConstantNode(constant));
        }
        if (operation is "regex")
        {
            return new RegexNode(field, "dotnet", "value", RegexOptions.CultureInvariant);
        }
        if (operation is "any" or "all")
        {
            return new QuantifierNode(operation == "any" ? QueryQuantifier.Any : QueryQuantifier.All, field, truth);
        }
        if (operation is "related")
        {
            return new RelatedNode(field, truth);
        }
        QueryStringOperation text = operation switch
        {
            "stringEqualOrdinal" => QueryStringOperation.EqualsOrdinal,
            "stringEqualOrdinalIgnoreCase" => QueryStringOperation.EqualsOrdinalIgnoreCase,
            "startsWithOrdinal" => QueryStringOperation.StartsWithOrdinal,
            "endsWithOrdinal" => QueryStringOperation.EndsWithOrdinal,
            "containsOrdinal" => QueryStringOperation.ContainsOrdinal,
            _ => throw new InvalidOperationException("Unknown test operation."),
        };
        return new StringNode(text, field, new ConstantNode(new StringConstant("value")));
    }
}
