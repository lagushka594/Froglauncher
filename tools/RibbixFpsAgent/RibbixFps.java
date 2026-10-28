package ribbix.fps;

import java.lang.instrument.Instrumentation;
import java.lang.reflect.Field;
import java.lang.reflect.Modifier;

/**
 * RibbixLauncher: замер FPS. Аргумент агента — "класс:поле" в именах, под которыми игра работает
 * (обфусцированные, intermediary Fabric или Mojang), например "fgo:aw". Раз в секунду печатает
 * "RIBBIXFPS <n>" — лаунчер читает это из лога. Только чтение статического int, игру не меняет.
 * Свой пакет обязателен: классы Minecraft лежат в безымянном пакете подписанного jar, и неподписанный
 * класс рядом с ними ломает запуск (SecurityException: signer information does not match).
 *
 * Сборка (JDK 9+): javac --release 8 -d out RibbixFps.java && jar cfm ribbix-fps-agent.jar manifest.txt -C out .
 */
public final class RibbixFps {
    public static void premain(String args, Instrumentation inst) {
        if (args == null || args.indexOf(':') < 0) return;
        final String cls = args.substring(0, args.indexOf(':'));
        final String field = args.substring(args.indexOf(':') + 1);
        final Instrumentation in = inst;
        Thread t = new Thread(new Runnable() {
            public void run() {
                Field f = null;
                while (true) {
                    try {
                        Thread.sleep(1000);
                        if (f == null) f = find(in, cls, field);
                        if (f != null) System.out.println("RIBBIXFPS " + f.getInt(null));
                    } catch (InterruptedException e) {
                        return;
                    } catch (Throwable e) {
                        f = null;
                    }
                }
            }
        }, "RibbixFps");
        t.setDaemon(true);
        t.start();
    }

    private static Field find(Instrumentation inst, String cls, String field) throws Exception {
        for (Class<?> c : inst.getAllLoadedClasses()) {
            if (!c.getName().equals(cls)) continue;
            Field f = c.getDeclaredField(field);
            if (!Modifier.isStatic(f.getModifiers()) || f.getType() != int.class) return null;
            f.setAccessible(true);
            return f;
        }
        return null;
    }
}
