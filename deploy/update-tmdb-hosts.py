#!/usr/bin/env python3
import json
import os
import tempfile
import urllib.parse
import urllib.request

HOSTS = ("api.themoviedb.org", "image.tmdb.org")
START = "# BEGIN lampac-tmdb-doh"
END = "# END lampac-tmdb-doh"


def resolve(host):
    query = urllib.parse.urlencode({"name": host, "type": "A"})
    request = urllib.request.Request(
        "https://cloudflare-dns.com/dns-query?" + query,
        headers={"Accept": "application/dns-json", "User-Agent": "lampac-tmdb-doh/1"},
    )
    with urllib.request.urlopen(request, timeout=15) as response:
        data = json.load(response)
    addresses = [item["data"] for item in data.get("Answer", []) if item.get("type") == 1 and item.get("data") != "127.0.0.1"]
    if not addresses:
        raise RuntimeError("No valid A record for " + host)
    return addresses[0]


def main():
    path = "/etc/hosts"
    with open(path, encoding="utf-8") as source:
        lines = source.read().splitlines()
    clean, skipping = [], False
    for line in lines:
        if line == START:
            skipping = True
            continue
        if line == END:
            skipping = False
            continue
        if not skipping and not any(line.split("#", 1)[0].split()[1:] == [host] for host in HOSTS if line.split("#", 1)[0].split()):
            clean.append(line)
    block = [START] + [f"{resolve(host)} {host}" for host in HOSTS] + [END]
    fd, temporary = tempfile.mkstemp(prefix="hosts.", dir="/etc", text=True)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as target:
            target.write("\n".join(clean + block) + "\n")
        os.chmod(temporary, 0o644)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)


if __name__ == "__main__":
    main()
