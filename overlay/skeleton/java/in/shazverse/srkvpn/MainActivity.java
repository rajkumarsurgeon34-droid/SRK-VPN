package in.shazverse.srkvpn;

import android.Manifest;
import android.app.Activity;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.VpnService;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.text.TextUtils;
import android.view.View;
import android.view.WindowInsets;
import android.webkit.JavascriptInterface;
import android.webkit.WebChromeClient;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.widget.FrameLayout;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.StringReader;

import de.blinkt.openvpn.VpnProfile;
import de.blinkt.openvpn.core.ConfigParser;
import de.blinkt.openvpn.core.ConnectionStatus;
import de.blinkt.openvpn.core.IOpenVPNServiceInternal;
import de.blinkt.openvpn.core.OpenVPNService;
import de.blinkt.openvpn.core.ProfileManager;
import de.blinkt.openvpn.core.VPNLaunchHelper;
import de.blinkt.openvpn.core.VpnStatus;

/** SRK VPN main screen: a WebView UI on top of the ics-openvpn engine and the VPN Gate server list. */
public class MainActivity extends Activity implements VpnStatus.StateListener, VpnStatus.ByteCountListener {
    private static final int REQ_VPN = 70;
    private static final int REQ_NOTIF = 71;

    private final Handler ui = new Handler(Looper.getMainLooper());
    private final VpnGate gate = new VpnGate();
    private WebView web;
    private String pendingId;
    private volatile String lastState = "{\"state\":\"NOPROCESS\",\"level\":\"LEVEL_NOTCONNECTED\",\"msg\":\"\"}";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().setStatusBarColor(Color.parseColor("#0B1020"));
        getWindow().setNavigationBarColor(Color.parseColor("#0B1020"));

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(Color.parseColor("#0B1020"));
        web = new WebView(this);
        root.addView(web, new FrameLayout.LayoutParams(-1, -1));
        setContentView(root);
        root.setOnApplyWindowInsetsListener((v, insets) -> {
            int top, bottom, left, right;
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets i = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                top = i.top; bottom = i.bottom; left = i.left; right = i.right;
            } else {
                top = insets.getSystemWindowInsetTop(); bottom = insets.getSystemWindowInsetBottom();
                left = insets.getSystemWindowInsetLeft(); right = insets.getSystemWindowInsetRight();
            }
            v.setPadding(left, top, right, bottom);
            return insets;
        });

        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        web.setBackgroundColor(Color.parseColor("#0B1020"));
        web.setWebChromeClient(new WebChromeClient());
        web.addJavascriptInterface(new Bridge(), "SRK");
        web.loadUrl("file:///android_asset/www/index.html");

        if (Build.VERSION.SDK_INT >= 33 &&
                checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, REQ_NOTIF);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        VpnStatus.addStateListener(this);
        VpnStatus.addByteCountListener(this);
    }

    @Override
    protected void onPause() {
        VpnStatus.removeStateListener(this);
        VpnStatus.removeByteCountListener(this);
        super.onPause();
    }

    // ---- engine callbacks -------------------------------------------------------------

    @Override
    public void updateState(String state, String logmessage, int localizedResId, ConnectionStatus level, Intent intent) {
        try {
            JSONObject o = new JSONObject();
            o.put("state", state);
            o.put("level", level == null ? "UNKNOWN_LEVEL" : level.name());
            o.put("msg", logmessage == null ? "" : logmessage);
            lastState = o.toString();
            emit("state", lastState);
        } catch (Exception ignored) { }
    }

    @Override
    public void setConnectedVPN(String uuid) { }

    @Override
    public void updateByteCount(long in, long out, long diffIn, long diffOut) {
        emit("bytes", "{\"in\":" + in + ",\"out\":" + out + ",\"diffIn\":" + diffIn + ",\"diffOut\":" + diffOut + "}");
    }

    private void emit(String event, String json) {
        final String js = "window.__vpn && window.__vpn(" + JSONObject.quote(event) + "," + (json == null ? "null" : json) + ")";
        ui.post(() -> { if (web != null) web.evaluateJavascript(js, null); });
    }

    private void emitError(String event, Exception e) {
        String m = e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage();
        emit(event, "{\"error\":" + JSONObject.quote(m) + "}");
    }

    // ---- connect / disconnect ------------------------------------------------------------

    private void requestConnect(String id) {
        pendingId = id;
        Intent prep = VpnService.prepare(this);
        if (prep != null) {
            try {
                startActivityForResult(prep, REQ_VPN);
            } catch (Exception e) {
                emitError("connectError", e);
            }
        } else {
            startServer(id);
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        if (requestCode == REQ_VPN) {
            if (resultCode == RESULT_OK && pendingId != null) startServer(pendingId);
            else emit("connectError", "{\"error\":\"VPN permission was not granted\",\"permission\":true}");
        }
    }

    private void startServer(String id) {
        new Thread(() -> {
            try {
                VpnGate.Server srv = gate.get(id);
                if (srv == null) throw new Exception("Server no longer in list — refresh");
                ConfigParser cp = new ConfigParser();
                cp.parseConfig(new StringReader(srv.config));
                VpnProfile vp = cp.convertProfile();
                vp.mName = "SRK VPN · " + srv.country;
                // VPN Gate relays run SoftEther with older OpenVPN settings: be lenient on TLS and
                // keep their CBC cipher as a fallback for servers that do not negotiate ciphers.
                vp.mTlSCertProfile = "insecure";
                if (!TextUtils.isEmpty(vp.mCipher)) {
                    String c = vp.mCipher;
                    if (TextUtils.isEmpty(vp.mDataCiphers)) vp.mDataCiphers = "AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305:" + c;
                    else if (!vp.mDataCiphers.contains(c)) vp.mDataCiphers += ":" + c;
                    vp.mUseCustomConfig = true;
                    vp.mCustomConfigOptions = (vp.mCustomConfigOptions == null ? "" : vp.mCustomConfigOptions)
                            + "\ndata-ciphers-fallback " + c + "\n";
                }
                int err = vp.checkProfile(this);
                if (err != de.blinkt.openvpn.R.string.no_error_found) throw new Exception(getString(err));
                ProfileManager.setTemporaryProfile(this, vp);
                VPNLaunchHelper.startOpenVpn(vp, getApplicationContext(), "SRK VPN: " + srv.country, true);
                emit("connecting", "{\"id\":" + JSONObject.quote(id) + "}");
            } catch (Exception e) {
                emitError("connectError", e);
            }
        }).start();
    }

    private void stopVpn() {
        ProfileManager.setConnectedVpnProfileDisconnected(this);
        Intent i = new Intent(this, OpenVPNService.class);
        i.setAction(OpenVPNService.START_SERVICE);
        final Context app = getApplicationContext();
        app.bindService(i, new ServiceConnection() {
            @Override
            public void onServiceConnected(ComponentName name, IBinder service) {
                try {
                    IOpenVPNServiceInternal.Stub.asInterface(service).stopVPN(false);
                } catch (Exception e) {
                    VpnStatus.logException(e);
                }
                try { app.unbindService(this); } catch (Exception ignored) { }
            }

            @Override
            public void onServiceDisconnected(ComponentName name) { }
        }, Context.BIND_AUTO_CREATE);
    }

    // ---- JavaScript bridge ------------------------------------------------------------------

    final class Bridge {
        @JavascriptInterface
        public void loadServers(boolean force) {
            new Thread(() -> {
                try {
                    emit("servers", gate.load(MainActivity.this, force));
                } catch (Exception e) {
                    emitError("serversError", e);
                }
            }).start();
        }

        @JavascriptInterface
        public void connect(String id) { ui.post(() -> requestConnect(id)); }

        @JavascriptInterface
        public void disconnect() { ui.post(MainActivity.this::stopVpn); }

        @JavascriptInterface
        public String state() { return lastState; }

        @JavascriptInterface
        public void checkIp() {
            new Thread(() -> {
                try {
                    JSONObject out = new JSONObject();
                    try {
                        JSONObject j = new JSONObject(VpnGate.download("https://ipwho.is/"));
                        out.put("ip", j.optString("ip"));
                        out.put("country", j.optString("country"));
                        out.put("cc", j.optString("country_code"));
                        out.put("city", j.optString("city"));
                        JSONObject conn = j.optJSONObject("connection");
                        out.put("isp", conn == null ? "" : conn.optString("isp"));
                    } catch (Exception first) {
                        JSONObject j = new JSONObject(VpnGate.download("https://ipinfo.io/json"));
                        out.put("ip", j.optString("ip"));
                        out.put("country", "");
                        out.put("cc", j.optString("country"));
                        out.put("city", j.optString("city"));
                        out.put("isp", j.optString("org"));
                    }
                    emit("ip", out.toString());
                } catch (Exception e) {
                    emitError("ipError", e);
                }
            }).start();
        }

        @JavascriptInterface
        public void toast(String msg) {
            ui.post(() -> Toast.makeText(MainActivity.this, msg, Toast.LENGTH_SHORT).show());
        }
    }

    @Override
    protected void onDestroy() {
        if (web != null) {
            web.removeJavascriptInterface("SRK");
            web.destroy();
            web = null;
        }
        super.onDestroy();
    }
}
