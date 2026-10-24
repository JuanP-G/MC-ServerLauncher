package com.mcserverlauncher.agent;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.lang.instrument.Instrumentation;
import java.lang.management.GarbageCollectorMXBean;
import java.lang.management.ManagementFactory;
import java.lang.management.MemoryPoolMXBean;
import java.lang.management.MemoryType;
import java.lang.management.MemoryUsage;
import java.lang.reflect.Method;
import java.nio.charset.Charset;
import java.nio.file.Files;
import java.nio.file.StandardCopyOption;
import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * MC Server Launcher's memory agent.
 *
 * <p>It does two small things inside the server's JVM, and nothing else:
 * <ul>
 * <li>every {@value #PERIOD_SECONDS} seconds it reads {@code control} (at most 1 KB of
 * {@code key=value} lines) and applies the three heap options it allows, validated, with
 * {@code HotSpotDiagnosticMXBean.setVMOption};</li>
 * <li>it writes {@code status.json} with what the JVM holds and has in use, the collector's totals
 * and the flags in effect, by writing a temporary file and renaming it.</li>
 * </ul>
 *
 * <p>The launcher decides; this only executes. It opens no socket, never calls {@code System.gc()}
 * and does not use {@link Instrumentation}. Everything is wrapped in {@code catch (Throwable)}: an
 * exception out of {@code premain} would stop the server from starting, and one out of the thread
 * would only stop the agent, which is the safe way for it to fail.
 *
 * <p>Source level 8, compiled with {@code --release 8}, so the one jar serves every Java the launcher
 * can start a server with. {@code com.sun.management} is reached by reflection: it is absent from
 * OpenJ9 and from some stripped runtimes, and the agent must still load there.
 */
public final class Agent implements Runnable {
    /** The agent's version, shown in {@code status.json}. */
    static final String VERSION = "1";

    static final int PERIOD_SECONDS = 5;

    /** The control file is read only up to this size. */
    static final int MAX_CONTROL_BYTES = 1024;

    private static final Charset UTF8 = Charset.forName("UTF-8");

    /** The only options a control file can set, and the range each accepts. */
    private static final String[] OPTIONS = {"MinHeapFreeRatio", "MaxHeapFreeRatio", "G1PeriodicGCInterval"};

    private static volatile String lastError = "";

    private final File dir;

    private Agent(File dir) {
        this.dir = dir;
    }

    /** Called by the JVM before {@code main}. Never throws. */
    public static void premain(String args, Instrumentation inst) {
        try {
            File dir = new File(args == null || args.trim().isEmpty() ? ".mcserverlauncher-memory" : args.trim());
            Thread t = new Thread(new Agent(dir), "mcsl-memory-agent");
            t.setDaemon(true);
            t.start();
        } catch (Throwable ignored) {
            // The server must start whatever happens here.
        }
    }

    @Override
    public void run() {
        Map<String, String> applied = new HashMap<String, String>();
        while (true) {
            try {
                applyControl(new File(dir, "control"), applied);
                writeStatus(new File(dir, "status.json"));
            } catch (Throwable e) {
                lastError = describe(e);
            }
            try {
                Thread.sleep(PERIOD_SECONDS * 1000L);
            } catch (InterruptedException e) {
                return;
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // control

    /** Reads the control file and applies the options that changed. */
    static void applyControl(File control, Map<String, String> applied) throws IOException {
        if (!control.isFile()) return;
        Map<String, String> wanted = parseControl(readLimited(control));
        for (Map.Entry<String, String> e : wanted.entrySet()) {
            if (e.getValue().equals(applied.get(e.getKey()))) continue;
            try {
                setVmOption(e.getKey(), e.getValue());
                applied.put(e.getKey(), e.getValue());
                lastError = "";
            } catch (Throwable ex) {
                // Remembered as applied too: a JVM that refuses an option (Java 8 has no periodic
                // collection) would otherwise be asked again every five seconds for ever.
                applied.put(e.getKey(), e.getValue());
                lastError = e.getKey() + ": " + describe(ex);
            }
        }
    }

    /**
     * The options in a control file's text that are allowed and valid. Unknown keys, bad numbers and
     * out-of-range values are dropped; a maximum below the minimum drops both ratios.
     */
    static Map<String, String> parseControl(String text) {
        Map<String, String> result = new LinkedHashMap<String, String>();
        for (String line : text.split("\\r?\\n")) {
            int eq = line.indexOf('=');
            if (eq <= 0) continue;
            String key = line.substring(0, eq).trim();
            String value = line.substring(eq + 1).trim();
            if (!isOption(key)) continue;
            Integer n = parseInt(value);
            if (n == null || !inRange(key, n.intValue())) continue;
            result.put(key, String.valueOf(n));
        }
        String min = result.get("MinHeapFreeRatio");
        String max = result.get("MaxHeapFreeRatio");
        if (min != null && max != null && Integer.parseInt(max) < Integer.parseInt(min)) {
            result.remove("MinHeapFreeRatio");
            result.remove("MaxHeapFreeRatio");
        }
        return result;
    }

    private static boolean isOption(String key) {
        for (String o : OPTIONS) if (o.equals(key)) return true;
        return false;
    }

    private static boolean inRange(String key, int value) {
        if ("G1PeriodicGCInterval".equals(key)) return value >= 0 && value <= 3600000;
        return value >= 0 && value <= 100;
    }

    private static Integer parseInt(String s) {
        if (s.isEmpty() || s.length() > 9) return null;
        for (int i = 0; i < s.length(); i++) if (s.charAt(i) < '0' || s.charAt(i) > '9') return null;
        return Integer.valueOf(s);
    }

    private static String readLimited(File f) throws IOException {
        InputStream in = new FileInputStream(f);
        try {
            byte[] buffer = new byte[MAX_CONTROL_BYTES];
            int total = 0;
            int n;
            while (total < buffer.length && (n = in.read(buffer, total, buffer.length - total)) > 0) total += n;
            return new String(buffer, 0, total, UTF8);
        } finally {
            in.close();
        }
    }

    // ---------------------------------------------------------------------------------------------
    // HotSpotDiagnosticMXBean, by reflection

    private static Class<?> diagnosticType() throws Exception {
        return Class.forName("com.sun.management.HotSpotDiagnosticMXBean");
    }

    private static Object diagnostic() throws Exception {
        return ManagementFactory.getPlatformMXBean(diagnosticType().asSubclass(java.lang.management.PlatformManagedObject.class));
    }

    // The methods are taken from the exported interface, not from the bean's own class: that one lives
    // in a module that does not export it, and invoking it through reflection would be refused.
    private static void setVmOption(String name, String value) throws Exception {
        Method m = diagnosticType().getMethod("setVMOption", String.class, String.class);
        m.invoke(diagnostic(), name, value);
    }

    private static String getVmOption(String name) {
        try {
            Method m = diagnosticType().getMethod("getVMOption", String.class);
            Object option = m.invoke(diagnostic(), name);
            return String.valueOf(Class.forName("com.sun.management.VMOption").getMethod("getValue").invoke(option));
        } catch (Throwable e) {
            return null;
        }
    }

    // ---------------------------------------------------------------------------------------------
    // status

    private static void writeStatus(File status) throws IOException {
        File dir = status.getAbsoluteFile().getParentFile();
        if (dir != null && !dir.isDirectory() && !dir.mkdirs()) return;

        Runtime rt = Runtime.getRuntime();
        MemoryUsage heap = ManagementFactory.getMemoryMXBean().getHeapMemoryUsage();

        // What each heap pool held right after its last collection: the nearest the JVM gets to
        // saying what is alive.
        long live = 0;
        boolean anyCollected = false;
        for (MemoryPoolMXBean pool : ManagementFactory.getMemoryPoolMXBeans()) {
            if (pool.getType() != MemoryType.HEAP) continue;
            MemoryUsage after = pool.getCollectionUsage();
            if (after == null) continue;
            live += Math.max(0, after.getUsed());
            anyCollected = true;
        }

        long gcCount = 0;
        long gcTime = 0;
        for (GarbageCollectorMXBean gc : ManagementFactory.getGarbageCollectorMXBeans()) {
            if (gc.getCollectionCount() > 0) gcCount += gc.getCollectionCount();
            if (gc.getCollectionTime() > 0) gcTime += gc.getCollectionTime();
        }

        StringBuilder json = new StringBuilder(512);
        json.append("{\"agent\":\"").append(VERSION).append('"');
        json.append(",\"time\":").append(System.currentTimeMillis());
        json.append(",\"java\":\"").append(escape(System.getProperty("java.version"))).append('"');
        json.append(",\"heapUsed\":").append(heap.getUsed());
        json.append(",\"heapCommitted\":").append(heap.getCommitted());
        json.append(",\"heapMax\":").append(heap.getMax());
        json.append(",\"liveAfterGc\":").append(anyCollected ? String.valueOf(live) : "null");
        json.append(",\"freeMemory\":").append(rt.freeMemory());
        json.append(",\"gcCount\":").append(gcCount);
        json.append(",\"gcTimeMs\":").append(gcTime);
        json.append(",\"flags\":{");
        boolean first = true;
        for (String option : OPTIONS) {
            String value = getVmOption(option);
            if (value == null) continue;
            if (!first) json.append(',');
            first = false;
            json.append('"').append(option).append("\":").append(escape(value).matches("-?\\d+") ? value : "\"" + escape(value) + "\"");
        }
        json.append('}');
        json.append(",\"lastError\":\"").append(escape(lastError)).append("\"}");

        File tmp = new File(dir, "status.json.tmp");
        Writer w = new OutputStreamWriter(new FileOutputStream(tmp), UTF8);
        try {
            w.write(json.toString());
        } finally {
            w.close();
        }
        try {
            Files.move(tmp.toPath(), status.toPath(), StandardCopyOption.REPLACE_EXISTING, StandardCopyOption.ATOMIC_MOVE);
        } catch (IOException e) {
            // A file system without an atomic move: a plain replace is still better than nothing.
            Files.move(tmp.toPath(), status.toPath(), StandardCopyOption.REPLACE_EXISTING);
        }
    }

    private static String describe(Throwable e) {
        Throwable cause = e.getCause() != null ? e.getCause() : e;
        String text = cause.getClass().getSimpleName() + (cause.getMessage() == null ? "" : ": " + cause.getMessage());
        return text.length() > 200 ? text.substring(0, 200) : text;
    }

    static String escape(String s) {
        if (s == null) return "";
        StringBuilder b = new StringBuilder(s.length() + 8);
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            switch (c) {
                case '"': b.append("\\\""); break;
                case '\\': b.append("\\\\"); break;
                case '\n': b.append("\\n"); break;
                case '\r': b.append("\\r"); break;
                case '\t': b.append("\\t"); break;
                default:
                    if (c < 0x20) b.append(String.format("\\u%04x", (int) c));
                    else b.append(c);
            }
        }
        return b.toString();
    }
}
