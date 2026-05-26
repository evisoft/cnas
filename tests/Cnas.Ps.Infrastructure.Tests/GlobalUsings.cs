global using Xunit;
global using FluentAssertions;
// IdHashHelper is used by virtually every integration test that seeds entities with
// national-identifier shadow columns (TOR SEC 035 follow-up); register globally so
// individual files don't need a using directive for the helper alone.
global using Cnas.Ps.Infrastructure.Tests.TestHelpers;
// R0671 — every ICallerContext test stub now needs RolesBasedAccessScope.Unscoped to
// satisfy the new IAccessScope property. Register globally so the seven existing stubs
// (and any new ones) pick it up without per-file using-directive churn.
global using Cnas.Ps.Infrastructure.AccessScope;
