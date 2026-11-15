package org.ggea.input;

import android.os.SystemClock;
import android.view.InputDevice;
import android.view.InputEvent;
import android.view.MotionEvent;
import java.io.File;
import java.lang.reflect.Method;

/** 运行于 adb shell 的 app_process；一次进程只拥有一个显示屏上的一根触点。 */
public final class PreciseDrag {
    private final Object input;
    private final Method inject;
    private final Method setDisplay;
    private final int display;
    private final File lease;

    private PreciseDrag(int display, File lease) throws Exception {
        Class<?> manager;
        try { manager = Class.forName("android.hardware.input.InputManagerGlobal"); }
        catch (ClassNotFoundException olderAndroid) { manager = Class.forName("android.hardware.input.InputManager"); }
        this.input = manager.getMethod("getInstance").invoke(null);
        this.inject = manager.getMethod("injectInputEvent", InputEvent.class, int.class);
        this.display = display;
        this.lease = lease;
        Method setter;
        try { setter = InputEvent.class.getMethod("setDisplayId", int.class); }
        catch (NoSuchMethodException olderAndroid) {
            if (display != 0) throw olderAndroid;
            setter = null;
        }
        this.setDisplay = setter;
    }

    private void send(int action, long downTime, float x, float y) throws Exception {
        MotionEvent event = MotionEvent.obtain(downTime, SystemClock.uptimeMillis(), action, x, y, 0);
        try {
            event.setSource(InputDevice.SOURCE_TOUCHSCREEN);
            if (setDisplay != null) setDisplay.invoke(event, display);
            // WAIT_FOR_RESULT 确认系统接收；游戏内的实际位移由调用方的新截图验证。
            if (!Boolean.TRUE.equals(inject.invoke(input, event, 1)))
                throw new IllegalStateException("touch injection rejected");
        } finally {
            event.recycle();
        }
    }

    private void checkLease() throws InterruptedException {
        if (lease.length() != 1) throw new InterruptedException("drag cancelled");
    }

    private void drag(int x1, int y1, int x2, int y2, int duration) throws Exception {
        Class<?> displays = Class.forName("android.hardware.display.DisplayManagerGlobal");
        Object manager = displays.getMethod("getInstance").invoke(null);
        Object info = displays.getMethod("getDisplayInfo", int.class).invoke(manager, display);
        if (info == null) throw new IllegalStateException("target display unavailable");
        int width = info.getClass().getField("logicalWidth").getInt(info);
        int height = info.getClass().getField("logicalHeight").getInt(info);
        if (Math.min(x1, x2) < 0 || Math.max(x1, x2) >= width ||
            Math.min(y1, y2) < 0 || Math.max(y1, y2) >= height)
            throw new IllegalArgumentException("drag outside target display");

        checkLease();
        long down = SystemClock.uptimeMillis();
        float x = x1, y = y1;
        send(MotionEvent.ACTION_DOWN, down, x, y);
        boolean released = false;
        try {
            long start = SystemClock.uptimeMillis();
            for (;;) {
                long now = SystemClock.uptimeMillis();
                long next = Math.min(start + duration, now + 12);
                SystemClock.sleep(Math.max(0, next - now));
                checkLease();
                double t = Math.min(1, (SystemClock.uptimeMillis() - start) / (double) duration);
                // 按单调时钟采样；注入耗时不会积累成每段的额外时长。
                double s = t * t * (3 - 2 * t);
                x = (float)(x1 + (x2 - x1) * s);
                y = (float)(y1 + (y2 - y1) * s);
                send(MotionEvent.ACTION_MOVE, down, x, y);
                if (t == 1) break;
            }
            // 末端静止一小段时间，避免以最后一次 MOVE 的速度离开画布。
            SystemClock.sleep(48);
            checkLease();
            send(MotionEvent.ACTION_UP, down, x2, y2);
            released = true;
        } finally {
            if (!released) send(MotionEvent.ACTION_CANCEL, down, x, y);
        }
    }

    private static void run(String[] args) throws Exception {
        if (args.length != 7) throw new IllegalArgumentException("invalid drag arguments");
        int duration = Integer.parseInt(args[5]);
        if (duration <= 0 || duration > 60000) throw new IllegalArgumentException("invalid duration");
        if (android.os.Looper.myLooper() == null) android.os.Looper.prepareMainLooper();
        int display = Integer.parseInt(args[0]);
        if (display < 0) {
            // 老模拟器的 activity 输出可能没有显示编号。仅在唯一可用屏确为 0 时接受。
            Class<?> displays = Class.forName("android.hardware.display.DisplayManagerGlobal");
            Object manager = displays.getMethod("getInstance").invoke(null);
            int[] ids;
            try { ids = (int[]) displays.getMethod("getDisplayIds", boolean.class).invoke(manager, false); }
            catch (NoSuchMethodException olderAndroid) {
                ids = (int[]) displays.getMethod("getDisplayIds").invoke(manager);
            }
            if (ids.length != 1 || ids[0] != 0) throw new IllegalStateException("ambiguous target display");
            display = 0;
        }
        PreciseDrag drag = new PreciseDrag(display, new File(args[6]));
        drag.drag(Integer.parseInt(args[1]), Integer.parseInt(args[2]),
            Integer.parseInt(args[3]), Integer.parseInt(args[4]), duration);
        System.out.println("GGEA_DRAG_OK");
    }

    public static void main(String[] args) {
        try { run(args); }
        catch (Exception error) {
            error.printStackTrace(System.err);
            System.exit(1);
        }
        // 框架可启动 Binder/显示管理线程；单次手势完成后明确结束本次 shell 进程。
        System.exit(0);
    }
}
