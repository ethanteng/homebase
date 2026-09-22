// Command uncloud-tunnel carries one Uncloud to the public internet over Tailscale Funnel.
//
// It is a Tailscale node in its own right — the library, not the daemon — so nothing has to be
// installed on the host and nobody has to meet a command line. Uncloud runs it, reads two kinds
// of line from its output, and stops it on the way out:
//
//	uncloud-tunnel: signin=https://login.tailscale.com/a/…   somebody has to allow this, once
//	uncloud-tunnel: url=https://name.tailnet.ts.net          the address it now answers to
//
// Everything else it prints goes to stderr, where Uncloud keeps the last of it to explain a
// failure with. The node's identity lives in -state, so the sign-in is asked for once and not
// again.
package main

import (
	"context"
	"flag"
	"fmt"
	"net"
	"net/http"
	"net/http/httputil"
	"net/url"
	"os"
	"os/signal"
	"strings"
	"syscall"
	"time"

	"tailscale.com/tsnet"
)

func main() {
	target := flag.String("target", "http://127.0.0.1:5210", "where Uncloud is listening")
	dir := flag.String("state", "", "directory holding this node's identity (required)")
	hostname := flag.String("hostname", "uncloud", "the name to ask the tailnet for")
	flag.Parse()

	if *dir == "" {
		fail("uncloud-tunnel needs -state, a directory it can keep this node's identity in.")
	}
	origin, err := url.Parse(*target)
	if err != nil || origin.Host == "" {
		fail("uncloud-tunnel needs -target to be a URL, not %q.", *target)
	}
	// Only this user: the directory holds a key that is this host's place in the tailnet.
	if err := os.MkdirAll(*dir, 0o700); err != nil {
		fail("uncloud-tunnel couldn't make %s: %v", *dir, err)
	}

	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()

	server := &tsnet.Server{Dir: *dir, Hostname: *hostname, Logf: note}
	defer server.Close()
	if err := server.Start(); err != nil {
		fail("uncloud-tunnel couldn't start a Tailscale node: %v", err)
	}

	// Bringing the node up blocks until somebody has allowed it, which on a first run is a
	// person going to find their phone. Watching alongside is what lets Uncloud put the link
	// in front of them rather than leaving them at a hung startup.
	watch, watching := context.WithCancel(ctx)
	defer watching()
	go func() {
		client, err := server.LocalClient()
		if err != nil {
			note("uncloud-tunnel couldn't read its own status: %v", err)
			return
		}
		announced := ""
		for watch.Err() == nil {
			if status, err := client.Status(watch); err == nil {
				if status.BackendState == "Running" {
					return
				}
				if status.AuthURL != "" && status.AuthURL != announced {
					announced = status.AuthURL
					say("signin=%s", status.AuthURL)
				}
			}
			select {
			case <-watch.Done():
				return
			case <-time.After(500 * time.Millisecond):
			}
		}
	}()

	status, err := server.Up(ctx)
	if err != nil {
		fail("uncloud-tunnel couldn't bring this host onto the tailnet: %v", err)
	}
	watching()
	name := strings.TrimSuffix(status.Self.DNSName, ".")

	listener, err := server.ListenFunnel("tcp", ":443")
	if err != nil {
		fail("uncloud-tunnel brought %s onto the tailnet but couldn't open it to the internet: %v\n"+
			"Funnel has to be allowed for this tailnet, and HTTPS certificates turned on. "+
			"Both are in the Tailscale admin console, under Access controls and DNS.", name, err)
	}
	defer listener.Close()

	// Only once it is actually carrying traffic, because Uncloud answers to this name from the
	// moment it hears it.
	say("url=https://%s", name)

	carry := &http.Server{Handler: &httputil.ReverseProxy{Rewrite: func(r *httputil.ProxyRequest) {
		r.SetURL(origin)
		// The name the browser asked for, not the loopback address behind it: Uncloud answers
		// to its public name and refuses everything else.
		r.Out.Host = r.In.Host
		// TLS ended here, at this process, on this machine — Tailscale's relays carried bytes
		// they could not read. Uncloud is told so, or it would refuse every sign-in as
		// cross-site against the https:// origin the browser sent.
		r.Out.Header.Set("X-Forwarded-Proto", "https")
		r.Out.Header.Del("X-Forwarded-Host")
		// Set from the connection and never appended to what arrived: a client's own claim
		// about its address would otherwise let anybody spend somebody else's sign-in throttle,
		// or dodge their own.
		if client, _, err := net.SplitHostPort(r.In.RemoteAddr); err == nil {
			r.Out.Header.Set("X-Forwarded-For", client)
		} else {
			r.Out.Header.Del("X-Forwarded-For")
		}
	}}}
	go func() {
		<-ctx.Done()
		carry.Close()
	}()
	if err := carry.Serve(listener); err != nil && ctx.Err() == nil {
		fail("uncloud-tunnel stopped carrying traffic: %v", err)
	}
}

// line composes one of the two things Uncloud reads. Its shape is a contract with
// RemoteAccessOptions.BuiltinAddress and SignInPrompt on the other side, which read the address
// and the link out of it; main_test.go holds both to the same patterns, because a disagreement
// here is a host answering to a name no browser ever sends.
func line(format string, args ...any) string {
	return "uncloud-tunnel: " + fmt.Sprintf(format, args...)
}

// say is for the two lines Uncloud reads. Everything else is note.
func say(format string, args ...any) {
	fmt.Fprintln(os.Stdout, line(format, args...))
}

func note(format string, args ...any) {
	fmt.Fprintf(os.Stderr, strings.TrimSuffix(format, "\n")+"\n", args...)
}

func fail(format string, args ...any) {
	note(format, args...)
	os.Exit(1)
}
