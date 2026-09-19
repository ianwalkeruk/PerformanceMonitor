/*
 * Copyright (c) 2026 Erik Darling, Darling Data LLC
 *
 * This file is part of the SQL Server Performance Monitor.
 *
 * Licensed under the MIT License. See LICENSE file in the project root for full license information.
 */

using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using PerformanceMonitor.Darling.Service;
using PerformanceMonitor.Darling.Storage;
using PerformanceMonitor.Darling.Viewer;
using Xunit;

namespace Darling.Tests;

/// <summary>
/// V127: the PagerDuty auto-resolve opt-in on <c>config.config_notification</c> — when set, a connection
/// recovery ("Server Restored") is sent as a PagerDuty <c>resolve</c> on the same dedup_key its
/// "Server Unreachable" trigger opened, closing the incident; when unset (the default) the recovery is an
/// info-severity trigger and the incident stays open, so the tool never auto-resolves a third-party
/// incident unasked.
///
/// <para>This file carries the "I am the top rung" claims that moved off
/// <see cref="SelfDiskWarnGbFloorRungTests"/> (V126) when this rung landed, the same handoff that file
/// received from <see cref="CollectorDatabaseScopeRungTests"/> (V125) — a fully-migrated store must map to
/// EXACTLY this version, or the viewer's connect-time gate refuses a store that is actually current.</para>
/// </summary>
public sealed class PagerDutyAutoResolveRungTests
{
    private const int RungVersion = 127;
    private const int PreviousVersion = 126;

    /// <summary>This rung's sentinel ordinal in the viewer probe — the newest, so the last argument.</summary>
    private const int ProbeOrdinal = 102;

    private const string ResolveColumn = "pagerduty_auto_resolve";

    /* ---- the rung ------------------------------------------------------------------------------------ */

    [Fact]
    public void TheRungIsRegisteredAtTheTopOfADenseLadder()
    {
        var versions = PgMigrations.Scripts.Select(s => s.Version).ToList();

        Assert.Equal(
            "pagerduty-auto-resolve",
            PgMigrations.Scripts.Single(s => s.Version == RungVersion).Name);

        Assert.Equal(StorageVersion.SchemaVersion, PgMigrations.Scripts[^1].Version);
        Assert.Equal(StorageVersion.SchemaVersion, versions.Max());
        Assert.Equal(RungVersion, StorageVersion.SchemaVersion);

        Assert.Equal(versions.Distinct().OrderBy(v => v), versions);
    }

    /// <summary>
    /// The rung adds ONE non-null boolean column to the singleton notification row, schema-qualified, with
    /// FALSE as its default so every existing store keeps the shipped no-auto-resolve behaviour.
    /// </summary>
    [Fact]
    public void TheRungAddsTheColumn_SchemaQualified_WithFalseAsDefault()
    {
        var rung = PgMigrations.Scripts.Single(s => s.Version == RungVersion).Sql;

        /* Schema-qualified for the reason every config rung is: the migrate session's search_path puts
           collect first, so a bare name would resolve to the wrong schema (and the wrong ACL). */
        Assert.Equal(1, CountOf(rung, "ALTER TABLE config.config_notification"));
        Assert.DoesNotContain("ALTER TABLE config_notification", rung, StringComparison.Ordinal);

        Assert.Equal(1, CountOf(rung, "ADD COLUMN IF NOT EXISTS"));
        Assert.Contains(
            $"ADD COLUMN IF NOT EXISTS {ResolveColumn} boolean NOT NULL DEFAULT FALSE;",
            rung, StringComparison.Ordinal);

        /* A boolean toggle, not a secret: no column GRANT carve of its own, and no reload beacon of its
           own — V17's statement-level trg_bump_notification already bumps on any write here. */
        Assert.DoesNotContain("GRANT", rung, StringComparison.Ordinal);
        Assert.DoesNotContain("config_bump_version", rung, StringComparison.Ordinal);

        /* No data movement: a boolean ADD COLUMN with a constant default is metadata-only in PostgreSQL. */
        foreach (var shape in new[] { "UPDATE ", "DELETE ", "INSERT ", "CREATE INDEX" })
        {
            Assert.DoesNotContain(shape, rung, StringComparison.Ordinal);
        }
    }

    /* ---- the probe (three sites, top arm) ------------------------------------------------------------- */

    /// <summary>
    /// The viewer probe's three sites carry this rung's sentinel, and the map treats it as the TOP arm.
    ///
    /// <para>The probe asks the question, the caller reads the answer, the map has the parameter — three
    /// sites, and a sentinel present at only some of them shifts every LATER ordinal onto the wrong column.
    /// Miss all three and a fully-migrated store probes one rung short, so the connect-time gate refuses a
    /// store that is in fact current — permanently, because no later upgrade changes the answer.</para>
    /// </summary>
    [Fact]
    public void TheProbeMapsAFullyMigratedStoreToThisTopRung()
    {
        Assert.Contains($"column_name = '{ResolveColumn}'", ViewerDataService.StoreSchemaProbeSql, StringComparison.Ordinal);

        var viewer = RepoFile.ReadRepoFile("Darling", "PerformanceMonitor.Darling.Viewer", "ViewerDataService.cs");
        Assert.Contains($"reader.GetBoolean({ProbeOrdinal})", viewer, StringComparison.Ordinal);
        Assert.Contains("hasPagerDutyAutoResolve", viewer, StringComparison.Ordinal);

        Assert.Equal(StorageVersion.SchemaVersion, ViewerDataService.RequiredStoreSchemaVersion);

        var method = typeof(ViewerDataService)
            .GetMethod("MapProbedSchemaVersion", BindingFlags.NonPublic | BindingFlags.Static)!;
        var arity = method.GetParameters().Length;

        /* The top rung's sentinel IS the last argument. */
        Assert.Equal(ProbeOrdinal, arity - 1);

        /* Every sentinel true = a fully-migrated store, which must map to exactly this version. */
        var all = Enumerable.Repeat((object)true, arity).ToArray();
        Assert.Equal(StorageVersion.SchemaVersion, (int)method.Invoke(null, all)!);

        /* This rung's own arm answers for a store that stopped here. */
        var atThisRung = Enumerable.Range(0, arity).Select(i => (object)(i <= ProbeOrdinal)).ToArray();
        Assert.Equal(RungVersion, (int)method.Invoke(null, atThisRung)!);

        /* One rung behind: the same store WITHOUT this rung's sentinel reports the previous rung. */
        var behind = (object[])atThisRung.Clone();
        behind[ProbeOrdinal] = false;
        Assert.Equal(PreviousVersion, (int)method.Invoke(null, behind)!);

        /* And in the source, the arm sits ABOVE V126's — newest-first is the whole contract of that method —
           and returns this build's version rather than a literal that could drift from it. */
        var v127 = viewer.IndexOf("if (hasPagerDutyAutoResolve)", StringComparison.Ordinal);
        var v126 = viewer.IndexOf("if (hasSelfDiskWarnGbFloor)", StringComparison.Ordinal);
        Assert.True(v127 >= 0, "the viewer has no V127 sentinel arm — a fully-migrated store would map to 126");
        Assert.True(v126 >= 0, "the V126 arm is gone, so this pin is comparing against nothing");
        Assert.True(v127 < v126, "the V127 arm sits below V126's, so a current store maps one rung low");
        Assert.Contains(
            "return " + StorageVersion.SchemaVersion.ToString(CultureInfo.InvariantCulture) + ";",
            viewer[v127..], StringComparison.Ordinal);
    }

    /* ---- every notification-row surface names the column ---------------------------------------------- */

    /// <summary>
    /// EVERY surface that reads or writes the notification row names the column — the viewer INCLUDED. The
    /// wired lists drive ordinals or parameter positions, so a column added to one and not the others
    /// re-maps reads and writes at once. The column is non-secret, so unlike the routing key beside it, it
    /// stays in the read-only viewer role's grant in both the code generator and the provisioning script.
    /// </summary>
    [Fact]
    public void EveryNotificationRowSurfaceNamesTheColumn()
    {
        var service = RepoFile.ReadRepoFile(
            "Darling", "PerformanceMonitor.Darling.Service", "StoreConfigProvider.cs");
        Assert.Contains(ResolveColumn, service, StringComparison.Ordinal);
        Assert.Contains("PagerDutyAutoResolve = reader.GetBoolean(", service, StringComparison.Ordinal);

        var viewerSettings = RepoFile.ReadRepoFile(
            "Darling", "PerformanceMonitor.Darling.Viewer", "ViewerDataService.Notification.cs");
        Assert.Contains(ResolveColumn, ViewerDataService.NotificationSelectSql, StringComparison.Ordinal);
        Assert.Contains(ResolveColumn, ViewerDataService.NotificationSelectNoSecretSql, StringComparison.Ordinal);
        Assert.Contains(
            $"{ResolveColumn} = EXCLUDED.{ResolveColumn}", ViewerDataService.NotificationUpsertSql, StringComparison.Ordinal);
        Assert.Contains("PagerDutyAutoResolve = reader.GetBoolean(19)", viewerSettings, StringComparison.Ordinal);
        Assert.Contains("PagerDutyAutoResolve = reader.GetBoolean(12)", viewerSettings, StringComparison.Ordinal);

        var roles = RepoFile.ReadRepoFile(
            "Darling", "PerformanceMonitor.Darling.Service", "DarlingManagedRoles.cs");
        Assert.Contains(ResolveColumn, roles, StringComparison.Ordinal);

        var provision = RepoFile.ReadRepoFile("Darling", "tools", "provision-roles.sql");
        Assert.Contains(ResolveColumn, provision, StringComparison.Ordinal);
    }

    /* ---- the seam reaches the gate -------------------------------------------------------------------- */

    /// <summary>
    /// The settings adapter defaults to false (the shipped no-auto-resolve behaviour) and reads the live
    /// config, so a store reload reflects the operator's choice immediately.
    /// </summary>
    [Fact]
    public void TheSettingsSeamDefaultsToFalse_AndReadsTheConfig()
    {
        var config = new DarlingConfig();
        var settings = new DarlingAlertSettings(config);

        Assert.False(settings.PagerDutyAutoResolve);

        config.Webhooks.PagerDutyAutoResolve = true;
        Assert.True(settings.PagerDutyAutoResolve);
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var at = haystack.IndexOf(needle, StringComparison.Ordinal);
             at >= 0;
             at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
