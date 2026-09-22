package main

import (
	"regexp"
	"testing"
)

// The patterns from src/Homebase.Server/RemoteAccess.cs, copied verbatim. Uncloud reads this
// program's output with them, and the two halves are written in different languages by people
// who cannot see each other — so the contract is held here rather than assumed.
var (
	uncloudReadsAddress = regexp.MustCompile(`^uncloud-tunnel: url=https://([^\s/]+)/?$`)
	uncloudReadsSignIn  = regexp.MustCompile(`^uncloud-tunnel: signin=(\S+)$`)
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
