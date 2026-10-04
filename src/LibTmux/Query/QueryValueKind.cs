using System.Diagnostics.CodeAnalysis;

namespace LibTmux.Query;

/// <summary>Names the scalar value types accepted by the portable query vocabulary.</summary>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Names identify the fixed query schema scalar types, including the integer width.")]
public enum QueryValueKind
{
    /// <summary>A Boolean value.</summary>
    Boolean,

    /// <summary>A signed 64-bit integer.</summary>
    Int64,

    /// <summary>Ordinal text.</summary>
    String,

    /// <summary>An identifier tagged with its target entity kind.</summary>
    TypedId,
}
