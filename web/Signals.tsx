import { Icon } from './Icon';
import type { Signal } from './attention';
export function SignalChip({ signal }: { signal: Signal }) {
  return (
    <span className="signal-chip" data-tone={signal.tone} title={signal.title}>
      <Icon name={signal.icon} />
      {signal.label}
    </span>
  );
}
