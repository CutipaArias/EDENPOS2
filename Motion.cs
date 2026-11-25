using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace EdenPOS
{
    // =====================================================================================================================
    // MOVIMIENTO (guías de Apple, motion.md y feedback.md):
    //   · Con propósito: solo se anima lo que informa (qué cambió, dónde está el cursor, a qué pantalla se fue).
    //   · Breve: Motion.Fast 120 ms (hover), Motion.Normal 180 ms (selección), Motion.Flash 650 ms (resaltar un cambio).
    //   · Nunca bloquea: la acción ya ocurrió cuando empieza la animación; un nuevo cambio la reemplaza al instante.
    //   · Opcional: si Windows tiene desactivadas las animaciones (Theme.UseMotion = false) se salta al estado final.
    //   · Presionar NO se anima: la respuesta al presionar sigue siendo inmediata.
    // Uso:  Motion.Animate(control, "hover", Motion.Fast, t => { valor = t; control.Invalidate(); });
    //       HoverFade: progreso 0..1 que sigue a un objetivo (true/false) para fundir colores de hover.
    // =====================================================================================================================
    public static class Motion
    {
        public const int Fast = 120, Normal = 180, Flash = 650;

        private sealed class Run
        {
            public Control Owner;
            public string Key;
            public DateTime Start;
            public int Duration;
            public Action<double> Step;
            public Action Done;
        }

        private static readonly List<Run> Runs = new List<Run>();
        private static Timer timer;

        // Curva "ease-out" cúbica: arranca rápido y se asienta suave (sensación de respuesta inmediata).
        public static double EaseOut(double t) { t = Math.Max(0, Math.Min(1, t)); var u = 1 - t; return 1 - u * u * u; }

        // Anima de 0 a 1 llamando a step(t) con t ya suavizado. Reemplaza cualquier animación previa del mismo control y clave.
        public static void Animate(Control owner, string key, int durationMs, Action<double> step, Action done = null)
        {
            if (owner == null || step == null) return;
            Cancel(owner, key);
            if (!Theme.UseMotion || durationMs <= 0 || owner.IsDisposed || !owner.IsHandleCreated || !owner.Visible)
            {
                step(1);
                if (done != null) done();
                return;
            }
            Runs.Add(new Run { Owner = owner, Key = key, Start = DateTime.UtcNow, Duration = durationMs, Step = step, Done = done });
            step(0);
            EnsureTimer();
        }

        public static void Cancel(Control owner, string key)
        {
            Runs.RemoveAll(r => r.Owner == owner && r.Key == key);
        }

        private static void EnsureTimer()
        {
            if (timer == null)
            {
                timer = new Timer { Interval = 15 };   // ~60 fotogramas por segundo
                timer.Tick += (s, e) => Tick();
            }
            if (!timer.Enabled) timer.Start();
        }

        private static void Tick()
        {
            var now = DateTime.UtcNow;
            foreach (var run in Runs.ToArray())
            {
                if (!Runs.Contains(run)) continue;   // cancelada por otro paso de este mismo fotograma
                if (run.Owner.IsDisposed) { Runs.Remove(run); continue; }
                var t = (now - run.Start).TotalMilliseconds / run.Duration;
                try
                {
                    if (t >= 1)
                    {
                        Runs.Remove(run);
                        run.Step(1);
                        if (run.Done != null) run.Done();
                    }
                    else run.Step(EaseOut(t));
                }
                catch (Exception ex)
                {
                    Runs.Remove(run);   // una animación nunca debe tumbar la app
                    try { AppData.LogError(ex, "Animación"); } catch { }
                }
            }
            if (Runs.Count == 0) timer.Stop();
        }
    }

    // Progreso 0..1 que persigue un objetivo (hover sí/no) con una animación breve, para fundir colores sin saltos.
    // Si el objetivo cambia a mitad de camino, continúa desde donde iba (no vuelve a empezar).
    public sealed class HoverFade
    {
        private readonly Control owner;
        private readonly string key;
        private readonly int duration;
        private bool target;

        public HoverFade(Control owner, string key = "hover", int duration = Motion.Fast)
        {
            this.owner = owner; this.key = key; this.duration = duration;
        }

        public double Value { get; private set; }

        public void Set(bool on)
        {
            if (on == target) return;
            target = on;
            var from = Value;
            var to = on ? 1.0 : 0.0;
            Motion.Animate(owner, key, (int)(duration * Math.Abs(to - from)), t =>
            {
                Value = from + (to - from) * t;
                if (!owner.IsDisposed) owner.Invalidate();
            });
        }

        // Salto sin animación (p. ej. al deshabilitar el control).
        public void Reset()
        {
            target = false;
            Motion.Cancel(owner, key);
            Value = 0;
        }
    }
}
