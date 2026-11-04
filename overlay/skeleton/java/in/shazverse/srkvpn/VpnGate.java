package in.shazverse.srkvpn;

import android.content.Context;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * Loads the public VPN Gate server list (University of Tsukuba volunteer relay project)
 * and keeps each server's OpenVPN config in memory, keyed by id.
 */
final class VpnGate {
    private static final String[] SOURCES = {
            "https://www.vpngate.net/api/iphone/",
            "http://www.vpngate.net/api/iphone/",
    };
    private static final String CACHE = "vpngate-cache.csv";

    static final class Server {
        String id, host, ip, country, cc, proto = "udp", port = "";
        long score, speed, sessions, uptime;
        int ping;
        String config;
    }

    private final Map<String, Server> servers = new HashMap<>();
    private long fetchedAt;
    private boolean fromCache;

    synchronized Server get(String id) { return servers.get(id); }

    /** Downloads (or falls back to the cached copy) and returns a JSON summary without configs. */
    String load(Context ctx, boolean forceNetwork) throws Exception {
        String csv = null;
        Exception last = null;
        File cache = new File(ctx.getFilesDir(), CACHE);
        if (!forceNetwork && cache.exists() && System.currentTimeMillis() - cache.lastModified() < 20 * 60 * 1000) {
            csv = read(new FileInputStream(cache));
            fromCache = false;
            fetchedAt = cache.lastModified();
        }
        if (csv == null) {
            for (String src : SOURCES) {
                try {
                    csv = download(src);
                    if (csv != null && csv.contains("OpenVPN_ConfigData_Base64")) break;
                    csv = null;
                } catch (Exception e) { last = e; }
            }
            if (csv != null) {
                try (FileOutputStream out = new FileOutputStream(cache)) { out.write(csv.getBytes(StandardCharsets.UTF_8)); }
                fromCache = false;
                fetchedAt = System.currentTimeMillis();
            } else if (cache.exists()) {
                csv = read(new FileInputStream(cache));
                fromCache = true;
                fetchedAt = cache.lastModified();
            } else {
                throw last != null ? last : new Exception("Server list unavailable");
            }
        }
        return parse(csv);
    }

    private synchronized String parse(String csv) throws Exception {
        servers.clear();
        List<Server> list = new ArrayList<>();
        for (String line : csv.split("\n")) {
            line = line.trim();
            if (line.isEmpty() || line.startsWith("*") || line.startsWith("#")) continue;
            String[] f = line.split(",");
            if (f.length < 15) continue;
            Server s = new Server();
            s.host = f[0];
            s.ip = f[1];
            s.score = num(f[2]);
            s.ping = (int) num(f[3]);
            s.speed = num(f[4]);
            s.country = f[5];
            s.cc = f[6].toUpperCase();
            s.sessions = num(f[7]);
            s.uptime = num(f[8]);
            String b64 = f[f.length - 1];
            try {
                s.config = new String(Base64.decode(b64, Base64.DEFAULT), StandardCharsets.UTF_8);
            } catch (IllegalArgumentException e) { continue; }
            if (!s.config.contains("<ca>")) continue;
            for (String cl : s.config.split("\n")) {
                String t = cl.trim();
                if (t.startsWith("proto ")) s.proto = t.substring(6).trim();
                else if (t.startsWith("remote ")) {
                    String[] p = t.split("\\s+");
                    if (p.length >= 3) s.port = p[2];
                }
            }
            s.id = s.host + "-" + s.ip + "-" + s.port;
            if (servers.containsKey(s.id)) continue;
            servers.put(s.id, s);
            list.add(s);
        }
        Collections.sort(list, (a, b) -> Long.compare(b.score, a.score));
        JSONArray arr = new JSONArray();
        for (Server s : list) {
            JSONObject o = new JSONObject();
            o.put("id", s.id);
            o.put("host", s.host);
            o.put("ip", s.ip);
            o.put("country", s.country);
            o.put("cc", s.cc);
            o.put("score", s.score);
            o.put("ping", s.ping);
            o.put("speed", s.speed);
            o.put("sessions", s.sessions);
            o.put("uptime", s.uptime);
            o.put("proto", s.proto);
            o.put("port", s.port);
            arr.put(o);
        }
        JSONObject res = new JSONObject();
        res.put("servers", arr);
        res.put("fetchedAt", fetchedAt);
        res.put("fromCache", fromCache);
        return res.toString();
    }

    static String download(String src) throws Exception {
        HttpURLConnection c = (HttpURLConnection) new URL(src).openConnection();
        c.setConnectTimeout(12000);
        c.setReadTimeout(25000);
        c.setRequestProperty("User-Agent", "SRK-VPN/1.0 (Android)");
        try {
            if (c.getResponseCode() != 200) throw new Exception("HTTP " + c.getResponseCode());
            return read(c.getInputStream());
        } finally { c.disconnect(); }
    }

    static String read(InputStream in) throws Exception {
        try (InputStream is = in) {
            ByteArrayOutputStream bo = new ByteArrayOutputStream();
            byte[] buf = new byte[16384];
            int n;
            while ((n = is.read(buf)) > 0) bo.write(buf, 0, n);
            return bo.toString("UTF-8");
        }
    }

    private static long num(String s) {
        try { return Long.parseLong(s.trim()); } catch (Exception e) { return 0; }
    }
}
