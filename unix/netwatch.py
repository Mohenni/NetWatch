#!/usr/bin/env python3
"""NetWatch for Linux and macOS (experimental edition).

Shows which programs send and receive data, in a page in your own web browser.
Needs nothing but Python 3.7+ and tools that are part of the system:

  Linux : "ss" (package iproute2). Counts TCP traffic per program and per server.
          Run with sudo to see the programs of all users.
  macOS : "nettop" (built in). Counts TCP and UDP per program (no per-server list).

Differences from the Windows edition: no blocking, no installer wizard, and the
numbers are sampled once a second, so very short connections can be missed.

The page is served on 127.0.0.1 only and protected by a random token; nothing is
sent anywhere.  Licence: MIT.
"""

import argparse
import collections
import json
import os
import platform
import random
import re
import secrets
import shutil
import subprocess
import sys
import threading
import time
import webbrowser
from http.server import BaseHTTPRequestHandler, HTTPServer
from socketserver import ThreadingMixIn

VERSION = "1.0"
WEBSITE = "https://github.com/Mohenni/NetWatch"
BITCOIN_ADDRESS = "bc1qe53lfh6535w4h3py7pwfhcml32lpq9rty55l5p"
HISTORY = 60                       # samples kept per program
THROWAWAY_DIRS = ("/tmp/", "/var/tmp/", "/dev/shm/", "/private/tmp/", "/private/var/tmp/")


# ----------------------------------------------------------------------------- bookkeeping

class App:
    """One program (all its processes together)."""

    def __init__(self, key, name, path, system, unusual):
        self.key = key
        self.name = name
        self.path = path
        self.system = system
        self.unusual = unusual          # list of reasons, empty when nothing is unusual
        self.sent = 0
        self.received = 0
        self.sent_history = collections.deque([0] * HISTORY, maxlen=HISTORY)
        self.received_history = collections.deque([0] * HISTORY, maxlen=HISTORY)
        self.pending_sent = 0
        self.pending_received = 0
        self.last_sent = 0.0
        self.last_received = 0.0
        self.servers = {}               # address -> [sent, received, port]

    def add(self, sent, received, server=None, port=0):
        now = time.time()
        if sent > 0:
            self.sent += sent
            self.pending_sent += sent
            self.last_sent = now
        if received > 0:
            self.received += received
            self.pending_received += received
            self.last_received = now
        if server and (sent > 0 or received > 0):
            entry = self.servers.get(server)
            if entry is None:
                if len(self.servers) >= 2000:
                    return
                entry = self.servers[server] = [0, 0, port]
            entry[0] += max(sent, 0)
            entry[1] += max(received, 0)

    def tick(self):
        self.sent_history.append(self.pending_sent)
        self.received_history.append(self.pending_received)
        self.pending_sent = 0
        self.pending_received = 0


class Model:
    def __init__(self):
        self.lock = threading.Lock()
        self.apps = {}
        self.started = time.time()
        self.note = ""                  # shown at the top of the page (for example "run with sudo")
        self.interval = 1.0
        self.has_servers = True

    def app_for(self, name, path, system, unusual):
        key = path or name
        app = self.apps.get(key)
        if app is None:
            app = self.apps[key] = App(key, name, path, system, unusual)
        return app

    def tick(self):
        for app in self.apps.values():
            app.tick()

    def snapshot(self):
        with self.lock:
            now = time.time()
            rows = []
            for i, app in enumerate(self.apps.values()):
                if app.sent == 0 and app.received == 0:
                    continue
                servers = sorted(app.servers.items(), key=lambda kv: -(kv[1][0] + kv[1][1]))[:200]
                rows.append({
                    "id": app.key, "name": app.name, "path": app.path or "", "system": app.system,
                    "unusual": app.unusual,
                    "sent": app.sent, "received": app.received,
                    "sentRate": app.sent_history[-1] / self.interval,
                    "receivedRate": app.received_history[-1] / self.interval,
                    "sentHistory": list(app.sent_history), "receivedHistory": list(app.received_history),
                    "lastSent": now - app.last_sent if app.last_sent else -1,
                    "lastReceived": now - app.last_received if app.last_received else -1,
                    "servers": [{"address": a, "sent": v[0], "received": v[1], "port": v[2]} for a, v in servers],
                    "serverCount": len(app.servers),
                })
            return {"apps": rows, "seconds": int(now - self.started), "note": self.note,
                    "hasServers": self.has_servers, "interval": self.interval,
                    "platform": platform.system(), "version": VERSION}


# ----------------------------------------------------------------------------- process details

_process_cache = {}


def process_details(pid, fallback_name):
    """Returns (name, path, is_system, unusual_reasons) for a process id."""
    cached = _process_cache.get(pid)
    if cached and cached[0] == fallback_name:
        return cached[1]
    path, uid, deleted = "", None, False
    if sys.platform.startswith("linux"):
        try:
            path = os.readlink("/proc/%d/exe" % pid)
            if path.endswith(" (deleted)"):
                path, deleted = path[:-10], True
        except OSError:
            pass
        try:
            uid = os.stat("/proc/%d" % pid).st_uid
        except OSError:
            pass
    else:
        try:
            out = subprocess.run(["ps", "-p", str(pid), "-o", "uid=,comm="], capture_output=True, text=True, timeout=3).stdout.strip()
            if out:
                parts = out.split(None, 1)
                uid = int(parts[0])
                path = parts[1] if len(parts) > 1 else ""
        except (OSError, ValueError, subprocess.SubprocessError):
            pass
    name = os.path.basename(path) if path else fallback_name
    result = (name or fallback_name, path, is_system(uid, path), unusual_reasons(path, deleted))
    if len(_process_cache) > 5000:
        _process_cache.clear()
    _process_cache[pid] = (fallback_name, result)
    return result


def is_system(uid, path):
    """Programs that belong to the operating system itself (can be hidden in the page)."""
    if uid is not None and uid != 0 and (uid < 500 if sys.platform == "darwin" else uid < 1000):
        return True                     # service accounts
    if uid == 0:
        return True
    return path.startswith(("/System/", "/usr/libexec/", "/usr/lib/systemd/", "/lib/systemd/"))


def unusual_reasons(path, deleted=False):
    """A few plain facts that make a program worth a second look. A hint, not a virus scan."""
    reasons = []
    if deleted:
        reasons.append("Its program file was deleted while it is still running.")
    if path and path.startswith(THROWAWAY_DIRS):
        reasons.append("It runs from a temporary folder.")
    if path and "/Downloads/" in path:
        reasons.append("It runs from a download folder.")
    return reasons


# ----------------------------------------------------------------------------- Linux: ss

SS_USERS = re.compile(r'users:\(\("([^"]*)",pid=(\d+)')
SS_SENT = re.compile(r'\bbytes_sent:(\d+)')
SS_ACKED = re.compile(r'\bbytes_acked:(\d+)')
SS_RECEIVED = re.compile(r'\bbytes_received:(\d+)')


def split_endpoint(text):
    """'192.168.1.5%wlan0:443' or '[2a00::1]:443' -> ('192.168.1.5', 443)"""
    host, _, port = text.rpartition(":")
    host = host.strip("[]").split("%")[0]
    try:
        return host, int(port)
    except ValueError:
        return host, 0


def is_private(host):
    if host.startswith("::ffff:"):
        host = host[7:]
    if host in ("::1", "localhost") or host.startswith("127."):
        return "loopback"
    if host.startswith(("10.", "192.168.", "169.254.", "fe80:", "fc", "fd")):
        return "lan"
    if host.startswith("172."):
        try:
            if 16 <= int(host.split(".")[1]) <= 31:
                return "lan"
        except (IndexError, ValueError):
            pass
    return ""


def parse_ss(text):
    """Parses 'ss -H -t -i -n -p' output into (name, pid, local, peer, port, sent, received) tuples."""
    sockets = []
    records = []
    for line in text.splitlines():
        if not line.strip():
            continue
        if line[0] in " \t" and records:
            records[-1] += " " + line.strip()      # the statistics line belongs to the socket above it
        else:
            records.append(line.strip())
    for record in records:
        fields = record.split()
        if len(fields) < 5 or fields[0] in ("State", "Netid", "LISTEN"):
            continue
        users = SS_USERS.search(record)
        if not users:
            continue                               # socket of a process we are not allowed to see
        sent = SS_SENT.search(record) or SS_ACKED.search(record)
        received = SS_RECEIVED.search(record)
        peer_host, peer_port = split_endpoint(fields[4])
        sockets.append((users.group(1), int(users.group(2)), fields[3], peer_host, peer_port,
                        int(sent.group(1)) if sent else 0, int(received.group(1)) if received else 0))
    return sockets


class LinuxCollector:
    """Reads the kernel's per-connection byte counters once a second (TCP only)."""

    def __init__(self, model, include_lan):
        self.model = model
        self.include_lan = include_lan
        self.previous = None
        model.interval = 1.0
        if os.geteuid() != 0:
            model.note = ("Only your own programs are shown. Close this and start it with sudo "
                          "to see the programs of all users (system services, for example).")

    def poll(self):
        try:
            out = subprocess.run(["ss", "-H", "-t", "-i", "-n", "-p"], capture_output=True, text=True, timeout=10).stdout
        except (OSError, subprocess.SubprocessError) as error:
            self.model.note = "Could not run 'ss' (install the package iproute2): %s" % error
            return
        current = {}
        for name, pid, local, host, port, sent, received in parse_ss(out):
            current[(pid, local, host, port)] = (name, sent, received)
        with self.model.lock:
            for key, (name, sent, received) in current.items():
                pid, _, host, port = key
                kind = is_private(host)
                if kind == "loopback" or (kind == "lan" and not self.include_lan):
                    continue
                if self.previous is None:
                    continue                       # first look: only remember where the counters stand
                old = self.previous.get(key)
                delta_sent = sent - old[1] if old else sent
                delta_received = received - old[2] if old else received
                if delta_sent <= 0 and delta_received <= 0:
                    continue
                app = self.model.app_for(*process_details(pid, name))
                app.add(delta_sent, delta_received, host, port)
            self.model.tick()
        self.previous = current


# ----------------------------------------------------------------------------- macOS: nettop

NETTOP_NAME = re.compile(r'^(.*)\.(\d+)$')
NETTOP_TIME = re.compile(r'^\d{1,2}:\d{2}:\d{2}\.\d+$')      # the time stamp looks like "name.number" too


def parse_nettop(text):
    """Parses 'nettop -P -L 1 -x -n -J bytes_in,bytes_out' output into (name, pid, bytes_in, bytes_out)."""
    rows = []
    for line in text.splitlines():
        fields = [f.strip() for f in line.split(",")]
        for i, field in enumerate(fields):
            match = NETTOP_NAME.match(field)
            if not match or NETTOP_TIME.match(field):
                continue
            numbers = [f for f in fields[i + 1:] if f.isdigit()]
            if len(numbers) >= 2:
                rows.append((match.group(1), int(match.group(2)), int(numbers[0]), int(numbers[1])))
            break
    return rows


class MacCollector:
    """Asks nettop for each process's running totals every two seconds (TCP and UDP)."""

    def __init__(self, model, include_lan):
        self.model = model
        self.previous = None
        model.interval = 2.0
        model.has_servers = False

    def poll(self):
        try:
            out = subprocess.run(["nettop", "-P", "-L", "1", "-x", "-n", "-J", "bytes_in,bytes_out"],
                                 capture_output=True, text=True, timeout=15).stdout
        except (OSError, subprocess.SubprocessError) as error:
            self.model.note = "Could not run 'nettop': %s" % error
            return
        current = {}
        for name, pid, received, sent in parse_nettop(out):
            current[pid] = (name, sent, received)
        with self.model.lock:
            if self.previous is not None:
                for pid, (name, sent, received) in current.items():
                    old = self.previous.get(pid)
                    delta_sent = sent - old[1] if old else sent
                    delta_received = received - old[2] if old else received
                    if delta_sent <= 0 and delta_received <= 0:
                        continue
                    self.model.app_for(*process_details(pid, name)).add(delta_sent, delta_received)
            self.model.tick()
        self.previous = current


# ----------------------------------------------------------------------------- made-up data

class DemoCollector:
    """Invented traffic, to try the page without any privileges (--demo)."""

    def __init__(self, model, include_lan):
        self.model = model
        model.note = "Demo mode: every number here is invented. Nothing on this page is real traffic."
        rng = random.Random(7)
        self.rng = rng
        self.flows = []
        programs = [("firefox", "/usr/lib/firefox/firefox", False, []),
                    ("backup-agent", "/opt/backup/backup-agent", False, []),
                    ("systemd-timesyncd", "/usr/lib/systemd/systemd-timesyncd", True, []),
                    ("updater", "/tmp/updater", False, ["It runs from a temporary folder."]),
                    ("chat", "/opt/chat/chat", False, []),
                    ("code", "/usr/share/code/code", False, []),
                    ("packagekitd", "/usr/libexec/packagekitd", True, [])]
        servers = ["93.184.216.34", "203.0.113.20", "198.51.100.7", "2001:db8::10", "192.0.2.55"]
        for name, path, system, unusual in programs:
            app = model.app_for(name, path, system, unusual)
            for _ in range(rng.randint(1, 4)):
                self.flows.append((app, rng.choice(servers), 250 * 10 ** (rng.random() * 2.6), 2 + rng.random() * 20))

    def poll(self):
        with self.model.lock:
            for app, server, up, factor in self.flows:
                if self.rng.random() < 0.6:
                    sent = int(up * (0.4 + self.rng.random() * 1.2))
                    app.add(sent, int(sent * factor), server, 443)
            self.model.tick()


# ----------------------------------------------------------------------------- the page

PAGE = r"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>NetWatch</title>
<style>
:root{--bg:#12141a;--panel:#181b22;--hover:#1f232d;--sel:#262c3c;--line:#282c38;--text:#e8ebf2;--dim:#989fb0;--faint:#686f81;
--up:#ffa348;--down:#56a8ff;--danger:#ff6b6b;--good:#46d38f;--btn:#2c313f}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.4 system-ui,-apple-system,"Segoe UI",sans-serif}
header{display:flex;align-items:center;gap:6px;padding:10px 18px;border-bottom:1px solid var(--line);flex-wrap:wrap}
.tab{background:none;border:0;color:var(--dim);font:600 15px inherit;padding:10px 14px;cursor:pointer;border-bottom:3px solid transparent}
.tab.on{color:var(--text)}.tab.on.s{border-color:var(--up)}.tab.on.r{border-color:var(--down)}.tab small{font-weight:400;margin-left:8px}
.tab.on.s small{color:var(--up)}.tab.on.r small{color:var(--down)}
.spacer{flex:1}button.plain{background:none;border:1px solid var(--line);color:var(--dim);border-radius:6px;padding:7px 14px;cursor:pointer;font:inherit}
button.plain:hover{background:var(--hover);color:var(--text)}
#note{padding:10px 24px;background:#2a2217;color:var(--up);display:none}
#stats{display:flex;gap:48px;padding:16px 24px;flex-wrap:wrap}.stat b{display:block;font-size:24px;font-weight:600}.stat span{font-size:11px;letter-spacing:.04em;color:var(--faint);font-weight:600}
table{width:100%;border-collapse:collapse;background:var(--panel)}th{font-size:11px;letter-spacing:.04em;color:var(--faint);text-align:right;padding:9px 12px;border-bottom:1px solid var(--line);cursor:pointer;user-select:none;white-space:nowrap}
th:first-child,td:first-child{text-align:left;padding-left:22px}th:hover{color:var(--text);text-decoration:underline}th.sorted{color:var(--dim)}
td{padding:9px 12px;text-align:right;border-bottom:1px solid var(--line);white-space:nowrap}tr.row{cursor:pointer}tr.row:hover{background:var(--hover)}tr.row.sel{background:var(--sel)}
tr.unusual{background:linear-gradient(90deg,rgba(255,107,107,.34),rgba(255,107,107,0) 60%);box-shadow:inset 4px 0 0 var(--danger)}
.name{font-weight:600}.sub{color:var(--faint);font-size:12px}.pill{background:var(--danger);color:#141418;font-size:10px;font-weight:700;border-radius:9px;padding:2px 8px;margin-left:8px;vertical-align:middle}
.rate.s{color:var(--up)}.rate.r{color:var(--down)}.idle{color:var(--faint)}canvas{vertical-align:middle}
#detail{padding:14px 24px 4px;font-size:17px;font-weight:600}#detail small{font-weight:400;color:var(--faint);font-size:13px;margin-left:10px}
#servers td,#servers th{padding-top:7px;padding-bottom:7px}
footer{padding:16px 24px;color:var(--faint);font-size:12px;display:flex;gap:14px;align-items:center;flex-wrap:wrap}
footer code{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:5px 9px;color:var(--text);font-size:13px}footer a{color:var(--dim)}
.empty{padding:40px;text-align:center;color:var(--faint)}
</style></head><body>
<header>
 <button class="tab s on" id="tabS">&#8593; Sending<small id="rateS"></small></button>
 <button class="tab r" id="tabR">&#8595; Receiving<small id="rateR"></small></button>
 <span class="spacer"></span>
 <button class="plain" id="hide">Hide system processes</button>
</header>
<div id="note"></div>
<div id="stats"></div>
<table><thead><tr id="head"></tr></thead><tbody id="rows"></tbody></table>
<div id="detail"></div>
<table id="servers"><thead><tr><th>SERVER ADDRESS</th><th>PORT</th><th>SENT</th><th>RECEIVED</th></tr></thead><tbody id="serverRows"></tbody></table>
<footer>
 <span id="status"></span><span class="spacer"></span>
 <span>NetWatch is free and open source. Say thanks with Bitcoin:</span><code id="btc">__BTC__</code>
 <button class="plain" id="copy">Copy address</button><a href="__WEBSITE__" target="_blank" rel="noopener">Project page</a>
</footer>
<script>
const TOKEN="__TOKEN__";let state=null,tab="s",sortKey="total",sortDown=true,hideSystem=false,selected=null;
const $=id=>document.getElementById(id);
function bytes(n){if(n<1024)return Math.round(n)+" B";let v=n/1024,u="KB";for(const x of["MB","GB","TB"]){if(v>=1024){v/=1024;u=x}}return v.toFixed(v>=100?0:v>=10?1:2)+" "+u}
function rate(n){return n<1?"–":bytes(n)+"/s"}
function ago(s){if(s<0)return"";if(s<5)return"now";if(s<60)return Math.round(s)+" s ago";if(s<3600)return Math.round(s/60)+" min ago";return Math.round(s/3600)+" h ago"}
function el(tag,cls,text){const e=document.createElement(tag);if(cls)e.className=cls;if(text!==undefined)e.textContent=text;return e}
function spark(values,color){const c=document.createElement("canvas");c.width=150;c.height=28;const g=c.getContext("2d"),max=Math.max(2048,...values);
 g.strokeStyle="#282c38";g.beginPath();g.moveTo(0,27.5);g.lineTo(150,27.5);g.stroke();if(!values.some(v=>v>0))return c;
 g.beginPath();g.moveTo(0,28);values.forEach((v,i)=>g.lineTo(i*150/(values.length-1),27-Math.min(1,v/max)*25));g.lineTo(150,28);g.closePath();
 g.fillStyle=color+"44";g.fill();g.beginPath();values.forEach((v,i)=>{const x=i*150/(values.length-1),y=27-Math.min(1,v/max)*25;i?g.lineTo(x,y):g.moveTo(x,y)});
 g.strokeStyle=color;g.lineWidth=1.2;g.stroke();return c}
const columns=[["name","PROGRAM"],["chart","LAST MINUTE"],["now","NOW"],["total","TOTAL"],["servers","SERVERS"],["last","LAST ACTIVE"]];
function value(a,key){const s=tab==="s";switch(key){case"name":return a.name.toLowerCase();case"now":case"chart":return s?a.sentRate:a.receivedRate;
 case"total":return s?a.sent:a.received;case"servers":return a.serverCount;case"last":{const v=s?a.lastSent:a.lastReceived;return v<0?-1e18:-v}}}
function render(){if(!state)return;const s=tab==="s",color=s?"#ffa348":"#56a8ff";
 $("tabS").className="tab s"+(s?" on":"");$("tabR").className="tab r"+(s?"":" on");
 let apps=state.apps.filter(a=>(s?a.sent:a.received)>0);const all=apps.length;
 const totalRate=state.apps.reduce((t,a)=>t+(s?a.sentRate:a.receivedRate),0);
 $("rateS").textContent=rate(state.apps.reduce((t,a)=>t+a.sentRate,0));$("rateR").textContent=rate(state.apps.reduce((t,a)=>t+a.receivedRate,0));
 const hidden=apps.filter(a=>a.system&&!a.unusual.length).length;if(hideSystem)apps=apps.filter(a=>!a.system||a.unusual.length);
 apps.sort((a,b)=>{const x=value(a,sortKey),y=value(b,sortKey);const c=x<y?-1:x>y?1:0;return sortDown?-c:c});
 const stats=$("stats");stats.replaceChildren();
 for(const[label,text]of[[s?"SENT SINCE START":"RECEIVED SINCE START",bytes(apps.reduce((t,a)=>t+(s?a.sent:a.received),0))],[s?"SENDING NOW":"RECEIVING NOW",rate(totalRate)],[s?"PROGRAMS THAT SENT":"PROGRAMS THAT RECEIVED",String(all)]]){
  const d=el("div","stat");d.append(el("span","",label),el("b","",text));stats.append(d)}
 const head=$("head");head.replaceChildren();
 for(const[key,title]of columns){if(key==="servers"&&!state.hasServers)continue;const th=el("th",key===sortKey?"sorted":"",title+(key===sortKey?(sortDown?" ▾":" ▴"):""));
  th.title="Click to sort by this column";if(key!=="chart")th.onclick=()=>{if(sortKey===key)sortDown=!sortDown;else{sortKey=key;sortDown=key!=="name"}render()};head.append(th)}
 const body=$("rows");body.replaceChildren();
 if(!apps.length){const tr=el("tr"),td=el("td","empty","Nothing yet. Programs appear here as soon as they "+(s?"send":"receive")+" data.");td.colSpan=6;tr.append(td);body.append(tr)}
 for(const a of apps){const tr=el("tr","row"+(a.unusual.length?" unusual":"")+(a.id===selected?" sel":""));
  const first=el("td");const n=el("span","name",a.name);first.append(n);if(a.unusual.length){first.append(el("span","pill","UNUSUAL"));tr.title="Marked as unusual because:\n"+a.unusual.join("\n")+"\n\nThis is a hint, not proof that the program is harmful."}
  first.append(el("div","sub",a.path||(a.system?"system process":"")));tr.append(first);
  const chart=el("td");chart.append(spark(s?a.sentHistory:a.receivedHistory,color));tr.append(chart);
  const r=s?a.sentRate:a.receivedRate;tr.append(el("td",r>=1?"rate "+tab:"idle",rate(r)),el("td","name",bytes(s?a.sent:a.received)));
  if(state.hasServers)tr.append(el("td","",String(a.serverCount)));tr.append(el("td","",ago(s?a.lastSent:a.lastReceived)));
  tr.onclick=()=>{selected=a.id;render()};body.append(tr)}
 const sel=state.apps.find(a=>a.id===selected),detail=$("detail"),servers=$("servers");detail.replaceChildren();
 if(!state.hasServers){servers.style.display="none"}else{servers.style.display="";const rows=$("serverRows");rows.replaceChildren();
  if(sel){detail.append(document.createTextNode(s?"Where "+sel.name+" sends data":"Where "+sel.name+" receives data from"),el("small","",sel.serverCount+" servers"));
   for(const v of[...sel.servers].sort((x,y)=>s?y.sent-x.sent:y.received-x.received)){const tr=el("tr");tr.append(el("td","",v.address),el("td","",String(v.port||"")),el("td","",bytes(v.sent)),el("td","",bytes(v.received)));rows.append(tr)}}
  else detail.append(el("small","","Click a program to see which servers it talks to."))}
 const note=$("note");note.textContent=state.note;note.style.display=state.note?"block":"none";
 const unusual=state.apps.filter(a=>a.unusual.length).length;
 $("status").textContent="Watching for "+(state.seconds<60?state.seconds+" s":Math.round(state.seconds/60)+" min")+(hideSystem?" · "+hidden+" system processes hidden":"")+(unusual?" · "+unusual+" unusual, marked in red":"");
 $("hide").textContent=hideSystem?"Show system processes":"Hide system processes"}
async function refresh(){try{const r=await fetch("/api?t="+TOKEN,{cache:"no-store"});if(r.ok){state=await r.json();render()}}catch(e){$("status").textContent="NetWatch was closed. This page no longer updates."}}
$("tabS").onclick=()=>{tab="s";render()};$("tabR").onclick=()=>{tab="r";render()};$("hide").onclick=()=>{hideSystem=!hideSystem;render()};
$("copy").onclick=async()=>{try{await navigator.clipboard.writeText($("btc").textContent);$("copy").textContent="Copied"}catch(e){const r=document.createRange();r.selectNode($("btc"));getSelection().removeAllRanges();getSelection().addRange(r);$("copy").textContent="Selected: press Ctrl+C"}setTimeout(()=>$("copy").textContent="Copy address",2500)};
refresh();setInterval(refresh,1000);
</script></body></html>
"""


class Server(ThreadingMixIn, HTTPServer):
    daemon_threads = True
    allow_reuse_address = True


def make_handler(model, token, port):
    page = (PAGE.replace("__TOKEN__", token).replace("__BTC__", BITCOIN_ADDRESS).replace("__WEBSITE__", WEBSITE)).encode("utf-8")
    allowed_hosts = ("127.0.0.1:%d" % port, "localhost:%d" % port)

    class Handler(BaseHTTPRequestHandler):
        def log_message(self, *args):
            pass

        def reply(self, code, content_type, body):
            self.send_response(code)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            # Only this computer, only with the right token: other users and web pages get nothing.
            path, _, query = self.path.partition("?")
            if self.headers.get("Host", "") not in allowed_hosts or ("t=" + token) not in query.split("&"):
                self.reply(403, "text/plain; charset=utf-8", b"Forbidden")
            elif path == "/":
                self.reply(200, "text/html; charset=utf-8", page)
            elif path == "/api":
                self.reply(200, "application/json", json.dumps(model.snapshot()).encode("utf-8"))
            else:
                self.reply(404, "text/plain; charset=utf-8", b"Not found")

    return Handler


def open_browser(url):
    """Opens the page in the browser of the person at the keyboard (not as root)."""
    user = os.environ.get("SUDO_USER")
    try:
        if hasattr(os, "geteuid") and os.geteuid() == 0 and user:
            opener = "open" if sys.platform == "darwin" else "xdg-open"
            if shutil.which(opener) and shutil.which("sudo"):
                keep = ["%s=%s" % (k, os.environ[k]) for k in ("DISPLAY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "XAUTHORITY") if k in os.environ]
                subprocess.Popen(["sudo", "-u", user, "env"] + keep + [opener, url], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return
        webbrowser.open(url)
    except Exception:
        pass


def self_test():
    """Checks the two output parsers against sample text. (The samples are written from the tools'
    documented formats; they are not captures from a real machine.)"""
    ss_sample = (
        'ESTAB 0 0 192.168.1.87:54321 142.250.185.68:443 users:(("firefox",pid=1234,fd=56))\n'
        '\t cubic wscale:8,7 rto:204 rtt:3.5/1.2 mss:1400 bytes_sent:4096 bytes_acked:4097 bytes_received:65536 segs_out:20\n'
        'ESTAB 0 0 [2a02:1234::20]:40000 [2a00:1450:4001:82a::200e]:443 users:(("chrome",pid=77,fd=9),("chrome",pid=78,fd=9))\n'
        '\t cubic bytes_acked:100 bytes_received:200\n'
        'ESTAB 0 0 127.0.0.1:5000 127.0.0.1:6000 users:(("local",pid=5,fd=3))\n'
        '\t cubic bytes_sent:1 bytes_received:1\n'
        'ESTAB 0 0 10.0.0.2%eth0:1111 10.0.0.9:22\n'
        '\t cubic bytes_sent:9 bytes_received:9\n')
    sockets = parse_ss(ss_sample)
    assert sockets[0] == ("firefox", 1234, "192.168.1.87:54321", "142.250.185.68", 443, 4096, 65536), sockets[0]
    assert sockets[1] == ("chrome", 77, "[2a02:1234::20]:40000", "2a00:1450:4001:82a::200e", 443, 100, 200), sockets[1]
    assert len(sockets) == 3 and sockets[2][0] == "local", sockets
    assert is_private("127.0.0.1") == "loopback" and is_private("192.168.1.1") == "lan" and is_private("172.20.1.1") == "lan"
    assert is_private("172.32.0.1") == "" and is_private("8.8.8.8") == "" and is_private("fe80::1") == "lan"

    nettop_sample = ("time,,bytes_in,bytes_out,\n"
                     "17:35:28.431091,apsd.89,43536,52414,\n"
                     "17:35:28.431212,Google Chrome H.1234,1000000,20000,\n"
                     "17:35:28.431300,kernel_task.0,0,0,\n")
    rows = parse_nettop(nettop_sample)
    assert rows[0] == ("apsd", 89, 43536, 52414), rows
    assert rows[1] == ("Google Chrome H", 1234, 1000000, 20000), rows
    assert parse_nettop(",bytes_in,bytes_out,\nmDNSResponder.321,5,6,\n") == [("mDNSResponder", 321, 5, 6)]

    assert unusual_reasons("/tmp/x") and not unusual_reasons("/usr/bin/curl")
    model = Model()
    app = model.app_for("a", "/usr/bin/a", False, [])
    app.add(10, 20, "1.2.3.4", 443)
    model.tick()
    snap = model.snapshot()
    assert snap["apps"][0]["sent"] == 10 and snap["apps"][0]["servers"][0]["address"] == "1.2.3.4"
    json.dumps(snap)
    print("self-test passed")


def check(seconds, include_lan):
    """Runs the real collector of this operating system for a while and reports what it saw."""
    model = Model()
    system = platform.system()
    if system == "Linux":
        collector = LinuxCollector(model, include_lan)
    elif system == "Darwin":
        collector = MacCollector(model, include_lan)
    else:
        print("check: unsupported system " + system)
        return 1
    end = time.time() + seconds
    polls = 0
    while time.time() < end:
        started = time.time()
        collector.poll()
        polls += 1
        time.sleep(max(0.1, model.interval - (time.time() - started)))
    snapshot = model.snapshot()
    print("system: %s   polls: %d   note: %s" % (system, polls, model.note or "-"))
    for app in sorted(snapshot["apps"], key=lambda a: -(a["sent"] + a["received"])):
        print("  %-28s sent %10d  received %10d  servers %3d  system=%s  %s"
              % (app["name"][:28], app["sent"], app["received"], app["serverCount"], app["system"], app["path"]))
    total = sum(a["sent"] + a["received"] for a in snapshot["apps"])
    print("programs with traffic: %d   bytes counted: %d" % (len(snapshot["apps"]), total))
    return 0 if snapshot["apps"] and total > 0 else 1


def main():
    parser = argparse.ArgumentParser(description="NetWatch: see which programs send and receive data.")
    parser.add_argument("--port", type=int, default=8765, help="port of the local page (default 8765)")
    parser.add_argument("--lan", action="store_true", help="also count traffic inside the home network (Linux)")
    parser.add_argument("--no-browser", action="store_true", help="do not open the browser, only print the address")
    parser.add_argument("--demo", action="store_true", help="show invented traffic (to try the page)")
    parser.add_argument("--selftest", action="store_true", help="check the parsers and exit")
    parser.add_argument("--check", type=int, metavar="SECONDS", default=0,
                        help="watch real traffic for this many seconds, print what was seen and exit "
                             "(exit code 1 if no program with traffic was found); used by the automatic tests")
    args = parser.parse_args()
    if args.selftest:
        self_test()
        return 0
    if args.check:
        return check(args.check, args.lan)

    model = Model()
    system = platform.system()
    if args.demo:
        collector = DemoCollector(model, args.lan)
    elif system == "Linux":
        if not shutil.which("ss"):
            print("NetWatch needs the 'ss' tool. Install the package 'iproute2' and try again.")
            return 1
        collector = LinuxCollector(model, args.lan)
    elif system == "Darwin":
        collector = MacCollector(model, args.lan)
    else:
        print("This edition is for Linux and macOS. On Windows use NetWatchSetup.exe from %s" % WEBSITE)
        return 1

    token = secrets.token_urlsafe(16)
    try:
        server = Server(("127.0.0.1", args.port), make_handler(model, token, args.port))
    except OSError as error:
        print("Could not open port %d (%s). Try another one with --port." % (args.port, error))
        return 1
    threading.Thread(target=server.serve_forever, daemon=True).start()
    url = "http://127.0.0.1:%d/?t=%s" % (args.port, token)
    print("NetWatch is running. Open this address in your browser:\n\n  %s\n\nPress Ctrl+C to stop." % url)
    if model.note:
        print("\nNote: " + model.note)
    if not args.no_browser:
        open_browser(url)
    try:
        while True:
            started = time.time()
            collector.poll()
            time.sleep(max(0.1, model.interval - (time.time() - started)))
    except KeyboardInterrupt:
        print("\nStopped.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
