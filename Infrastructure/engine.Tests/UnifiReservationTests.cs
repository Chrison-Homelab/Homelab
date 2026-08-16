using Homelab.Infrastructure.Converge;
using Homelab.Infrastructure.Shapes;
using Homelab.Infrastructure.Unifi;
using UnifiSharp.Legacy;
using Xunit;

namespace Homelab.Infrastructure.Tests;

// Pure tests for the DHCP-reservation reconcile (#416). No controller: everything
// that decides whether to write is a pure function of (shape, live rows).
public sealed class UnifiReservationTests
{
    private const string Mac = "bc:24:11:f6:9f:ae";
    private const string NetHomelab = "68e07cc49da6501d8c970f47";

    private static DesiredReservation Desired(
        string ip = "10.10.135.221",
        string? dns = "shell.devops.chrison.internal",
        string? parked = null,
        string? networkId = NetHomelab,
        string mac = Mac) =>
        new("shell", mac, ip, dns, "shell (CT 3003)", networkId, parked);

    private static UnifiUser LiveUser(
        string ip = "10.10.135.221",
        string? dns = "shell.devops.chrison.internal",
        bool useFixedIp = true,
        string mac = Mac,
        string? networkId = NetHomelab,
        string? name = "shell (CT 3003)") =>
        new()
        {
            Id = "live-1", Mac = mac, Name = name, UseFixedIp = useFixedIp, FixedIp = ip,
            NetworkId = networkId, LocalDnsRecord = dns,
            LocalDnsRecordEnabled = dns is null ? null : true,
        };

    // ---- the reconcile decision ----

    [Fact]
    public void Matching_live_state_is_a_no_op()
    {
        var item = UnifiReservations.Plan(Desired(), [LiveUser()]);
        Assert.Equal(ReservationAction.NoChange, item.Action);
        Assert.Empty(item.Changes);
    }

    [Fact]
    public void A_mac_with_no_row_is_a_create()
    {
        var item = UnifiReservations.Plan(Desired(), [LiveUser(mac: "aa:bb:cc:dd:ee:ff")]);
        Assert.Equal(ReservationAction.Create, item.Action);
    }

    [Fact]
    public void A_known_client_without_a_reservation_is_an_update_not_a_create()
    {
        // The controller keeps a row per MAC it has ever seen, so the common case for a
        // NEW reservation is a PUT onto an existing row — not a POST.
        var item = UnifiReservations.Plan(Desired(), [LiveUser(useFixedIp: false)]);

        Assert.Equal(ReservationAction.Update, item.Action);
        Assert.Equal("live-1", item.LiveId);
        Assert.Contains("use_fixedip: false → true", item.Changes);
    }

    [Fact]
    public void A_repointed_address_is_drift()
    {
        // The failure this exists to catch: the reservation still exists, so any
        // create-if-missing check would call it healthy.
        var item = UnifiReservations.Plan(Desired(ip: "10.10.135.221"), [LiveUser(ip: "10.10.99.99")]);

        Assert.Equal(ReservationAction.Update, item.Action);
        Assert.Contains("fixed_ip: 10.10.99.99 → 10.10.135.221", item.Changes);
    }

    [Fact]
    public void A_dropped_dns_record_is_drift()
    {
        // Six reservations carry the name services are targeted by, so losing one is
        // not cosmetic.
        var item = UnifiReservations.Plan(Desired(), [LiveUser(dns: null)]);
        Assert.Contains(item.Changes, c => c.StartsWith("local_dns_record:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_dns_record_present_but_disabled_is_drift()
    {
        var live = LiveUser() with { LocalDnsRecordEnabled = false };
        var item = UnifiReservations.Plan(Desired(), [live]);
        Assert.Contains("local_dns_record_enabled: false → true", item.Changes);
    }

    [Fact]
    public void A_shape_that_declares_no_dns_never_blanks_a_hand_set_record()
    {
        // Not claiming the field means not owning it — blanking a record we didn't ask
        // for would be a silent, load-bearing deletion on the next converge.
        var item = UnifiReservations.Plan(Desired(dns: null), [LiveUser(dns: "set.by.hand")]);
        Assert.Equal(ReservationAction.NoChange, item.Action);
    }

    // ---- guardrails ----

    [Fact]
    public void Parked_is_reported_and_never_written()
    {
        // Even when live state contradicts the shape outright.
        var item = UnifiReservations.Plan(
            Desired(parked: "rollback for the CT 6005 migration, #250"), [LiveUser(ip: "10.10.99.99")]);

        Assert.Equal(ReservationAction.Parked, item.Action);
        Assert.Empty(item.Changes);
        Assert.Contains("#250", item.Reason);
    }

    [Fact]
    public void Parked_is_decided_before_anything_else_can_block_it()
    {
        // A parked guest is stopped, so it often has no resolvable MAC — that must read
        // as parked, not as a blocking failure on every converge.
        var item = UnifiReservations.Plan(Desired(parked: "stopped on purpose", mac: ""), []);
        Assert.Equal(ReservationAction.Parked, item.Action);
    }

    [Fact]
    public void No_live_mac_blocks_rather_than_guessing()
    {
        var item = UnifiReservations.Plan(Desired(mac: ""), []);
        Assert.Equal(ReservationAction.Blocked, item.Action);
        Assert.Contains("MAC", item.Reason);
    }

    [Fact]
    public void An_unmapped_vlan_blocks_rather_than_writing_a_null_network()
    {
        var item = UnifiReservations.Plan(Desired(networkId: null), [LiveUser()]);
        Assert.Equal(ReservationAction.Blocked, item.Action);
    }

    // ---- normalisation: cosmetic differences must not read as drift ----

    [Fact]
    public void Mac_matching_ignores_case_and_separator()
    {
        var item = UnifiReservations.Plan(Desired(mac: "BC-24-11-F6-9F-AE"), [LiveUser(mac: Mac)]);
        Assert.Equal(ReservationAction.NoChange, item.Action);
    }

    [Fact]
    public void A_trailing_dot_on_a_dns_name_is_not_drift()
    {
        // Otherwise every converge rewrites the same record forever.
        var item = UnifiReservations.Plan(Desired(), [LiveUser(dns: "Shell.DevOps.Chrison.Internal.")]);
        Assert.Equal(ReservationAction.NoChange, item.Action);
    }

    [Fact]
    public void Ip_comparison_is_by_value_not_by_string()
    {
        Assert.True(UnifiReservations.IpEquals("10.10.0.1", "10.10.000.1"));
        Assert.False(UnifiReservations.IpEquals("10.10.0.1", "10.10.0.2"));
        Assert.True(UnifiReservations.IpEquals(null, ""));
    }

    // ---- VLAN -> network id ----

    [Fact]
    public void NetworkIdForVlan_maps_a_tag_and_skips_the_wan()
    {
        var networks = new[]
        {
            new UnifiNetwork { Id = "wan-1", Purpose = "wan", Vlan = "1010" }, // must never win
            new UnifiNetwork { Id = NetHomelab, Purpose = "corporate", Vlan = "1010" },
            new UnifiNetwork { Id = "iot", Purpose = "corporate", Vlan = "1040" },
        };

        Assert.Equal(NetHomelab, UnifiReservations.NetworkIdForVlan(networks, 1010));
        Assert.Equal("iot", UnifiReservations.NetworkIdForVlan(networks, 1040));
        Assert.Null(UnifiReservations.NetworkIdForVlan(networks, 4000));
    }

    [Fact]
    public void NetworkIdForVlan_treats_untagged_as_the_default_lan()
    {
        var networks = new[]
        {
            new UnifiNetwork { Id = "lan", Purpose = "corporate", Vlan = null },
            new UnifiNetwork { Id = "vlan10", Purpose = "corporate", Vlan = "10" },
        };
        Assert.Equal("lan", UnifiReservations.NetworkIdForVlan(networks, null));
    }

    // ---- orphan report ----

    [Fact]
    public void Orphans_are_reservations_no_shape_accounts_for()
    {
        var live = new[]
        {
            LiveUser(ip: "10.10.135.221"),                                  // declared
            LiveUser(ip: "10.40.169.225", mac: "bc:24:11:22:e6:6b", name: "leapmotor-mate (CT 4100)"),
            LiveUser(ip: "10.10.0.5", useFixedIp: false, mac: "de:ad:be:ef:00:01"), // not a reservation
        };

        var orphans = UnifiReservations.OrphanCandidates(live, declaredIps: ["10.10.135.221"]);

        var only = Assert.Single(orphans);
        Assert.Equal("10.40.169.225", only.FixedIp);
    }

    [Fact]
    public void Orphans_accept_either_identity()
    {
        // The report runs from the shapes, which know an address but never a MAC; the
        // converge path knows the MAC. Either must count as "declared".
        var live = new[] { LiveUser(ip: "10.10.135.221") };

        Assert.Empty(UnifiReservations.OrphanCandidates(live, declaredMacs: [Mac]));
        Assert.Empty(UnifiReservations.OrphanCandidates(live, declaredIps: ["10.10.135.221"]));
        Assert.Single(UnifiReservations.OrphanCandidates(live, declaredIps: ["10.10.0.99"]));
    }

    // ---- the write body ----

    [Fact]
    public void ToUser_sends_only_the_fields_we_own()
    {
        var body = UnifiReservations.ToUser(Desired());

        Assert.True(body.UseFixedIp);
        Assert.Equal("10.10.135.221", body.FixedIp);
        Assert.Equal(NetHomelab, body.NetworkId);
        Assert.True(body.LocalDnsRecordEnabled);
        Assert.Null(body.Hostname);   // the controller's own columns stay untouched
        Assert.Null(body.LastIp);
        Assert.Null(body.Id);
    }

    [Fact]
    public void ToUser_omits_the_dns_toggle_when_no_record_is_declared()
    {
        var body = UnifiReservations.ToUser(Desired(dns: null));
        Assert.Null(body.LocalDnsRecord);
        Assert.Null(body.LocalDnsRecordEnabled);
    }

    // ---- shape wiring ----

    [Fact]
    public void Declared_reads_the_primary_nic_and_every_extra_interface()
    {
        var shape = new Shape
        {
            Metadata = new ShapeMetadata { Name = "homeassistant" },
            Spec = new LxcSpec
            {
                Network = new NetworkSpec { Vlan = 1010, Reservation = new ReservationSpec { FixedIp = "10.10.0.21" } },
                Networks =
                {
                    new NetworkInterfaceSpec { Tag = 1010 },   // net0 — no reservation
                    new NetworkInterfaceSpec { Tag = 1040, Reservation = new ReservationSpec { FixedIp = "10.40.0.22" } },
                },
            },
        };

        var declared = UnifiReservationReconciler.Declared(shape).ToList();

        Assert.Equal(2, declared.Count);
        Assert.Equal((0, "10.10.0.21", 1010), (declared[0].Index, declared[0].Reservation.FixedIp, declared[0].Vlan));
        // Index 1 matters: it decides which netN's MAC gets read off the live guest.
        Assert.Equal((1, "10.40.0.22", 1040), (declared[1].Index, declared[1].Reservation.FixedIp, declared[1].Vlan));
    }

    [Fact]
    public void A_member_adding_only_a_reservation_keeps_the_stack_network_defaults()
    {
        // The trap this guards: `network` used to be inherited all-or-nothing, so a
        // member declaring `network.reservation` alone forfeited bridge/vlan/ipv4 and
        // would have been rendered onto the wrong network entirely.
        var dir = Directory.CreateTempSubdirectory("shape-merge-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "stack.yaml"),
                """
                apiVersion: homelab/v1
                kind: Stack
                metadata:
                  name: Test
                spec:
                  defaults:
                    node: hpe-01
                    network:
                      bridge: vmbr0
                      vlan: 1010
                      ipv4: dhcp
                      gateway: 10.10.0.1
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "thing.lxc.yaml"),
                """
                apiVersion: homelab/v1
                kind: LXC
                metadata:
                  name: thing
                spec:
                  app: thing
                  ctid: 9001
                  network:
                    reservation:
                      fixedIp: 10.10.5.5
                """);

            var member = Assert.Single(ShapeLoader.LoadStack(dir.FullName).Members);

            Assert.Equal("10.10.5.5", member.Spec.Network!.Reservation!.FixedIp);
            Assert.Equal(1010, member.Spec.Network.Vlan);        // inherited, not dropped
            Assert.Equal("vmbr0", member.Spec.Network.Bridge);
            Assert.Equal("dhcp", member.Spec.Network.Ipv4);
            Assert.Equal("10.10.0.1", member.Spec.Network.Gateway);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_stack_default_never_hands_every_member_the_same_address()
    {
        var dir = Directory.CreateTempSubdirectory("shape-merge-res-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "stack.yaml"),
                """
                apiVersion: homelab/v1
                kind: Stack
                metadata:
                  name: Test
                spec:
                  defaults:
                    node: hpe-01
                    network:
                      vlan: 1010
                      reservation:
                        fixedIp: 10.10.9.9
                """);
            File.WriteAllText(Path.Combine(dir.FullName, "thing.lxc.yaml"),
                """
                apiVersion: homelab/v1
                kind: LXC
                metadata:
                  name: thing
                spec:
                  app: thing
                  ctid: 9002
                """);

            var member = Assert.Single(ShapeLoader.LoadStack(dir.FullName).Members);
            Assert.Null(member.Spec.Network!.Reservation);
            Assert.Empty(UnifiReservationReconciler.Declared(member));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Declared_reads_a_vm_shape_too()
    {
        // VmSpec carries the same NetworkSpec/NetworkInterfaceSpec types, so `reservation:`
        // parses and validates on a VM shape whether or not anything reads it. If the audit
        // didn't, a reservation parked for a retired VM would be reported as an orphan on
        // every single run — and a report that cries wolf stops being read.
        var vm = new VmShape
        {
            Metadata = new ShapeMetadata { Name = "homeassistant" },
            Spec = new VmSpec
            {
                Networks =
                {
                    new NetworkInterfaceSpec
                    {
                        Reservation = new ReservationSpec { FixedIp = "192.168.179.102", Parked = "rollback for CT 6005, #250" },
                    },
                    new NetworkInterfaceSpec { Tag = 1040 },   // no reservation
                    new NetworkInterfaceSpec
                    {
                        Tag = 1010,
                        Reservation = new ReservationSpec { FixedIp = "10.10.0.20", Parked = "rollback for CT 6005, #250" },
                    },
                },
            },
        };

        var declared = UnifiReservationReconciler.Declared(vm).ToList();

        Assert.Equal(2, declared.Count);
        Assert.Equal([0, 2], declared.Select(d => d.Index));   // index survives the gap
        Assert.All(declared, d => Assert.True(d.Reservation.IsParked));
    }

    [Fact]
    public void A_vm_shapes_primary_nic_reservation_is_read_as_net0()
    {
        var vm = new VmShape
        {
            Metadata = new ShapeMetadata { Name = "vm" },
            Spec = new VmSpec
            {
                Network = new NetworkSpec { Vlan = 1010, Reservation = new ReservationSpec { FixedIp = "10.10.0.7" } },
            },
        };

        var only = Assert.Single(UnifiReservationReconciler.Declared(vm));
        Assert.Equal((0, "10.10.0.7", 1010), (only.Index, only.Reservation.FixedIp, only.Vlan));
    }

    [Fact]
    public void PlanSteps_says_parked_rather_than_promising_a_write()
    {
        var shape = new Shape
        {
            Metadata = new ShapeMetadata { Name = "ha-vm" },
            Spec = new LxcSpec
            {
                Network = new NetworkSpec
                {
                    Vlan = 1010,
                    Reservation = new ReservationSpec { FixedIp = "10.10.0.20", Parked = "rollback, #250" },
                },
            },
        };

        var step = Assert.Single(UnifiReservationReconciler.PlanSteps(shape));
        Assert.Contains("PARKED", step);
        Assert.Contains("#250", step);
    }
}
