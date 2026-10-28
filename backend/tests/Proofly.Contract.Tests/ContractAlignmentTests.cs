using System.Reflection;
using Proofly.Data.Audit;
using Proofly.Domain.Enums;

namespace Proofly.Contract.Tests;

/// <summary>The contract file, the embedded copy and the code vocabularies must say the same thing.</summary>
public class ContractAlignmentTests
{
    [Fact]
    public void The_contract_embedded_in_the_API_is_the_docs_file()
    {
        using var s = typeof(Program).Assembly.GetManifestResourceStream("openapi.yaml") ?? throw new InvalidOperationException("openapi.yaml is not embedded.");
        using var r = new StreamReader(s);
        Assert.Equal(ContractDoc.Text, r.ReadToEnd());
    }

    [Fact]
    public void Audit_events_named_by_operations_are_known_event_types()
    {
        var declared = ContractDoc.EnumOf("AuditEventType");
        foreach (var op in ContractDoc.Ops.Where(o => o.AuditEvent is not null))
        {
            Assert.Contains(op.AuditEvent!, declared);
            Assert.Contains(op.AuditEvent!, AuditVocabulary.EventTypes);
        }
    }

    [Fact]
    public void Audit_vocabulary_equals_the_contract()
    {
        Assert.Equal(ContractDoc.EnumOf("AuditEventType").Order(), AuditVocabulary.EventTypes.Order());
        Assert.Equal(ContractDoc.EnumOf("AuditSubjectKind").Order(), AuditVocabulary.SubjectKinds.Order());
    }

    public static IEnumerable<object[]> DomainEnums() =>
    [
        [typeof(Severity)], [typeof(ControlArea)], [typeof(ControlState)], [typeof(RunState)], [typeof(SignOffState)],
        [typeof(ExceptionStatus)], [typeof(ResolutionKind)], [typeof(TransactionKind)], [typeof(MatchStatus)],
        [typeof(AdjustmentStatus)], [typeof(SafeguardingState)], [typeof(DeliveryState)], [typeof(ActorType)],
        [typeof(Role)], [typeof(Permission)],
    ];

    [Theory]
    [MemberData(nameof(DomainEnums))]
    public void Domain_enum_wire_names_equal_the_contract_enum(Type type)
    {
        var wire = Enum.GetValues(type).Cast<object>().Select(v => type == typeof(Permission) ? WireNames.ToPermission((Permission)v)
            : type == typeof(ControlArea) ? WireNames.ToLowerSnake((ControlArea)v)
            : (string)typeof(WireNames).GetMethod(nameof(WireNames.ToUpperSnake))!.MakeGenericMethod(type).Invoke(null, [v])!).ToList();
        Assert.Equal(ContractDoc.EnumOf(type.Name).Order(StringComparer.Ordinal), wire.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_Domain_enum_with_a_contract_counterpart_is_checked_above()
    {
        var checkedNames = DomainEnums().Select(d => ((Type)d[0]).Name).ToHashSet();
        var domainEnums = typeof(Severity).Assembly.GetTypes().Where(t => t.IsEnum && t.Namespace == typeof(Severity).Namespace && t.GetCustomAttribute<FlagsAttribute>() is null);
        var unchecked_ = domainEnums.Select(t => t.Name).Where(n => ContractDoc.Root["components"]!["schemas"]![n] is not null && !checkedNames.Contains(n)).ToList();
        Assert.True(unchecked_.Count == 0, "Add these Domain enums to DomainEnums(): " + string.Join(", ", unchecked_));
    }
}
