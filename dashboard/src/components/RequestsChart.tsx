import { useState } from "react";
import { formatNumber } from "../format";

interface Point { day: string; requests: number }

const HEIGHT = 180;
const AXIS = 28;
const TOP = 12;

function niceMax(value: number): number {
  if (value <= 4) return 4;
  const magnitude = 10 ** Math.floor(Math.log10(value));
  const step = [1, 2, 2.5, 5, 10].find((s) => s * magnitude * 4 >= value)! * magnitude;
  return step * 4;
}

/**
 * Requests per day: a single series, so one hue and no legend box (the title names it).
 * Each bar has a hover/focus tooltip; the usage table below the chart is the accessible data view.
 */
export function RequestsChart({ points, title = "Requests per day", hideTitle = false }: { points: Point[]; title?: string; hideTitle?: boolean }) {
  const [active, setActive] = useState<number | null>(null);
  const max = niceMax(Math.max(0, ...points.map((p) => p.requests)));
  const ticks = [0, 0.25, 0.5, 0.75, 1].map((f) => f * max);
  const plotHeight = HEIGHT - AXIS - TOP;
  const slot = 100 / Math.max(points.length, 1);
  const labelEvery = Math.ceil(points.length / 6);

  return (
    <figure className="chart" aria-label={`${title}, ${points.length} days`}>
      <figcaption className={hideTitle ? "visually-hidden" : "chart-title"}>{title}</figcaption>
      <div className="chart-body">
        {/* Labels share the gridlines' y coordinates so each sits centred on its line. */}
        <div className="chart-y" aria-hidden="true" style={{ height: HEIGHT }}>
          {ticks.map((t) => (
            <span key={t} style={{ top: TOP + plotHeight - (t / max) * plotHeight }}>{formatNumber(t)}</span>
          ))}
        </div>
        <div className="chart-plot" style={{ height: HEIGHT }} onMouseLeave={() => setActive(null)}>
          <svg width="100%" height={HEIGHT} role="img" aria-hidden="true" preserveAspectRatio="none">
            {ticks.map((t) => {
              const y = TOP + plotHeight - (t / max) * plotHeight;
              return <line key={t} x1="0" x2="100%" y1={y} y2={y} className="chart-grid" />;
            })}
          </svg>
          {points.map((p, i) => {
            const h = (p.requests / max) * plotHeight;
            return (
              <button
                key={p.day}
                type="button"
                className={`chart-hit ${active === i ? "is-active" : ""}`}
                style={{ left: `${i * slot}%`, width: `${slot}%`, top: TOP, height: plotHeight }}
                onMouseEnter={() => setActive(i)}
                onFocus={() => setActive(i)}
                onBlur={() => setActive(null)}
                aria-label={`${p.day}: ${formatNumber(p.requests)} requests`}
              >
                <span className="chart-bar" style={{ height: p.requests > 0 ? Math.max(h, 2) : 0 }} />
              </button>
            );
          })}
          <div className="chart-x" aria-hidden="true">
            {points.map((p, i) => (
              <span key={p.day} style={{ left: `${(i + 0.5) * slot}%` }}>
                {i % labelEvery === 0 || i === points.length - 1 ? p.day.slice(5) : ""}
              </span>
            ))}
          </div>
          {active !== null && (
            <div
              className="chart-tooltip"
              role="status"
              style={{ left: `${Math.min(Math.max((active + 0.5) * slot, 10), 90)}%` }}
            >
              <span className="muted small">{points[active].day}</span>
              <strong>{formatNumber(points[active].requests)}</strong> requests
            </div>
          )}
        </div>
      </div>
    </figure>
  );
}
