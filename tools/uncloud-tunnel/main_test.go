package main

import (
	"regexp"
	"strings"
	"testing"
)

// The patterns from src/Homebase.Server/RemoteAccess.cs, copied verbatim. Uncloud reads this
// program's output with them, and the two halves are written in different languages by people
// who cannot see each other — so the contract is held here rather than assumed.
var (
	uncloudReadsAddress = regexp.MustCompile(`^uncloud-tunnel: url=https://([^\s/]+)/?$`)
	uncloudReadsSignIn  = regexp.MustCompile(`^uncloud-tunnel: signin=(\S+)$`)
	uncloudReadsAllow   = regexp.MustCompile(`^uncloud-tunnel: allow=(\S+)$`)
	uncloudReadsTrouble = regexp.MustCompile(`^uncloud-tunnel: trouble=(.+)$`)
)

func TestUncloudReadsTheAddressAsABareName(t *testing.T) {
	// Uncloud compares what it reads here against the Host header a browser sends, which
	// carries no scheme. Announcing one would refuse every request through the tunnel.
	found := uncloudReadsAddress.FindStringSubmatch(line("url=https://%s", "home.tail9f3a.ts.net"))
	if found == nil {
		t.Fatalf("Uncloud cannot read %q", line("url=https://%s", "home.tail9f3a.ts.net"))
	}
	if found[1] != "home.tail9f3a.ts.net" {
		t.Fatalf("Uncloud would answer to %q, which is not a name any browser asks for", found[1])
	}
}

func TestUncloudReadsTheSignInAsAWholeLink(t *testing.T) {
	// This one is put in front of a person to follow, so it keeps its scheme.
	const link = "https://login.tailscale.com/a/10692893011e9b"
	found := uncloudReadsSignIn.FindStringSubmatch(line("signin=%s", link))
	if found == nil || found[1] != link {
		t.Fatalf("Uncloud cannot read the sign-in out of %q", line("signin=%s", link))
	}
}

func TestUncloudReadsTheFunnelLinkAsAWholeLink(t *testing.T) {
	// The second yes, and a different one: this one is about the tailnet rather than this host,
	// so Uncloud has to be able to tell the two apart and say which is being waited on.
	const link = "https://login.tailscale.com/f/funnel?node=nodekey%3Aabc123"
	found := uncloudReadsAllow.FindStringSubmatch(line("allow=%s", link))
	if found == nil || found[1] != link {
		t.Fatalf("Uncloud cannot read the funnel link out of %q", line("allow=%s", link))
	}
	if uncloudReadsSignIn.MatchString(line("allow=%s", link)) {
		t.Fatal("Uncloud would take the funnel link for a sign-in and ask the wrong thing of somebody")
	}
}

func TestFailSaysWhyInOneLineUncloudCanRead(t *testing.T) {
	// fail's own wording wraps over several lines, and the panel reads one. Whatever is left
	// after flattening is the only account the person who can fix this ever sees.
	const message = "uncloud-tunnel brought home.tail9f3a.ts.net onto the tailnet\nbut couldn't " +
		"open it to the internet: Funnel not available; HTTPS must be enabled."
	said := line("trouble=%s", strings.Join(strings.Fields(message), " "))
	found := uncloudReadsTrouble.FindStringSubmatch(said)
	if found == nil {
		t.Fatalf("Uncloud cannot read the trouble out of %q", said)
	}
	if strings.ContainsAny(found[1], "\r\n") {
		t.Fatalf("the trouble arrives in pieces: %q", found[1])
	}
	if !strings.Contains(found[1], "HTTPS must be enabled") {
		t.Fatalf("the part that says what to do went missing: %q", found[1])
	}
}
