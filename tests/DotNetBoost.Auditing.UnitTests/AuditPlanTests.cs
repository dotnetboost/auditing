using DotNetBoost.Auditing.Core.Metadata;

namespace DotNetBoost.Auditing.UnitTests;

public class AuditPlanTests
{
    [Fact]
    public void A_property_with_no_attribute_is_recorded() =>
        Assert.Equal(AuditPropertyRule.Record, AuditPlan.For(typeof(PersonEntity)).RuleFor(nameof(PersonEntity.Email)));

    [Fact]
    public void AuditRedact_redacts() =>
        Assert.Equal(AuditPropertyRule.Redact, AuditPlan.For(typeof(PersonEntity)).RuleFor(nameof(PersonEntity.PasswordHash)));

    [Fact]
    public void AuditIgnore_on_a_property_ignores_it() =>
        Assert.Equal(AuditPropertyRule.Ignore, AuditPlan.For(typeof(PersonEntity)).RuleFor(nameof(PersonEntity.LastSeenAt)));

    [Fact]
    public void A_property_the_type_does_not_have_is_recorded() =>
        // A store may report a shadow property or a computed column, neither of which can carry an
        // attribute. The price is that a misspelling is recorded rather than refused.
        Assert.Equal(AuditPropertyRule.Record, AuditPlan.For(typeof(Product)).RuleFor("NoSuchProperty"));

    [Fact]
    public void AuditIgnore_on_a_class_marks_it_ignored()
    {
        Assert.True(AuditPlan.For(typeof(Heartbeat)).Ignored);
        Assert.False(AuditPlan.For(typeof(Product)).Ignored);
    }

    [Fact]
    public void Audited_marks_the_class_opted_in()
    {
        Assert.True(AuditPlan.For(typeof(Invoice)).OptedIn);
        Assert.False(AuditPlan.For(typeof(Product)).OptedIn);
    }

    [Fact]
    public void AuditRetention_is_read_as_days()
    {
        Assert.Equal(TimeSpan.FromDays(90), AuditPlan.For(typeof(SearchQuery)).Retention);
        Assert.Null(AuditPlan.For(typeof(Product)).Retention);
    }

    [Fact]
    public void A_retention_of_zero_days_is_refused() =>
        Assert.Throws<InvalidOperationException>(() => AuditPlan.For(typeof(ZeroRetention)));

    [Fact]
    public void Ignore_and_redact_on_one_property_is_refused()
    {
        // Two different statements about a secret. Guessing which was meant risks writing it down.
        var error = Assert.Throws<InvalidOperationException>(() => AuditPlan.For(typeof(Contradiction)));
        Assert.Contains("Keep one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_plan_knows_whether_it_has_any_property_rules_at_all()
    {
        Assert.True(AuditPlan.For(typeof(PersonEntity)).HasPropertyRules);
        Assert.False(AuditPlan.For(typeof(Product)).HasPropertyRules);
    }
}
