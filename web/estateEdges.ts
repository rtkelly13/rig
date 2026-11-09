import { serviceEdgePath } from '@rtkelly-labs/design-system';
import type { ServiceGraphLayout } from '@rtkelly-labs/design-system';

/*
 * Lines for the estate graph, drawn on top of `layoutServiceGraph`'s placement.
 *
 * The design-system layout places boxes well and draws every edge as one S-curve from the
 * middle of the source's right side to the middle of the target's left side. Two things go
 * wrong with that once the estate has more than a handful of repositories:
 *
 * - Every line out of a box leaves from the same point, so five consumers read as one
 *   thick line until they fan out, and where they fan out they cross.
 * - A dependency that skips a layer is a curve across the layers in between, and it runs
 *   straight through whatever boxes happen to stand there.
 *
 * So this routes instead: right angles, each vertical run (a trunk) standing in the gutter
 * between two layers rather than inside one, trunks sharing a gutter spread into lanes, and
 * a long line carried across the layers it skips along a channel no box stands in.
 * docs/estate/graph-rendering.md has the reasoning; the generic version is the vault's
 * `graph-layout` skill.
 */

/** One drawn line: the path, and the two boxes it joins. */
export interface RoutedEdge {
  from: string;
  to: string;
  d: string;
  /** The lowest y the line reaches; below the boxes when it had to take an overflow channel. */
  bottom: number;
}

export interface RouteOptions {
  /** The gap the layout was given between layers: the gutter a trunk stands in. */
  columnGap: number;
  /** The gap the layout was given between boxes in one layer. */
  rowGap: number;
}

/** Ports keep this far from a box's corners, so a line never looks like it meets the corner. */
const PORT_INSET = 0.2;

/** How far apart two trunks in one gutter, or two lines in one channel, are held. */
const LANE = 10;

/** Clear space kept between a channel and the boxes either side of it. */
const CLEARANCE = 6;

interface Placed {
  x: number;
  y: number;
  layer: number;
}

interface Trunk {
  gutter: number;
  top: number;
  bottom: number;
  x: number;
}

interface Route {
  from: string;
  to: string;
  source: { x: number; y: number };
  target: { x: number; y: number };
  /** The y the line crosses skipped layers at; null when it skips none. */
  channel: number | null;
  /** Vertical runs, with x filled in once every trunk in the gutter is known. */
  trunks: Trunk[];
}

function overlaps(a0: number, a1: number, b0: number, b1: number): boolean {
  return a0 < b1 && b0 < a1;
}

/**
 * Where the ports on one side of a box go: spread down the middle of the side, ordered by
 * where the line's other end sits, so the lines leave in the order they are going and do
 * not cross each other on the way out.
 */
function spread(count: number, top: number, height: number): number[] {
  const band = height * (1 - 2 * PORT_INSET);
  return Array.from(
    { length: count },
    (_, at) => top + height * PORT_INSET + (band * (at + 1)) / (count + 1),
  );
}

/**
 * The clear horizontal bands across *layers*: every y no box in them stands at, as
 * `[top, bottom]` pairs, including the open space above and below every box.
 */
function freeBands(
  placed: Placed[],
  layers: Set<number>,
  nodeHeight: number,
  height: number,
  rowGap: number,
): [number, number][] {
  const blocked = placed
    .filter((node) => layers.has(node.layer))
    .map((node) => [node.y - CLEARANCE, node.y + nodeHeight + CLEARANCE] as const)
    .sort((a, b) => a[0] - b[0]);
  const bands: [number, number][] = [];
  let cursor = -rowGap;
  for (const [top, bottom] of blocked) {
    if (top > cursor) bands.push([cursor, top]);
    cursor = Math.max(cursor, bottom);
  }
  bands.push([cursor, Math.max(cursor, height) + rowGap]);
  return bands;
}

/**
 * Route every forward edge of *layout* as right angles in the gutters. Back edges — there
 * should be none, since cycles are folded into one box before layout — keep the layout's
 * own loop under the graph.
 */
export function routeEstateEdges(layout: ServiceGraphLayout, options: RouteOptions): RoutedEdge[] {
  const { nodeWidth, nodeHeight } = layout;
  const placed = new Map<string, Placed>(layout.nodes.map((node) => [node.id, node]));
  const centre = (id: string) => (placed.get(id)?.y ?? 0) + nodeHeight / 2;
  const gutterLeft = (layer: number) => layer * (nodeWidth + options.columnGap) + nodeWidth;

  const forward = layout.edges.filter((edge) => !edge.back);

  // Ports. Each side is ordered by the other end's centre — a good enough proxy for where
  // the line is heading, since the channel a long line takes is chosen to be near it.
  const outPorts = new Map<string, number>();
  const inPorts = new Map<string, number>();
  const assign = (
    ports: Map<string, number>,
    side: 'from' | 'to',
    other: 'from' | 'to',
  ) => {
    const bySide = new Map<string, typeof forward>();
    for (const edge of forward) {
      const list = bySide.get(edge[side]) ?? [];
      list.push(edge);
      bySide.set(edge[side], list);
    }
    for (const [id, list] of bySide) {
      list.sort((a, b) => centre(a[other]) - centre(b[other]) || a[other].localeCompare(b[other]));
      const ys = spread(list.length, placed.get(id)?.y ?? 0, nodeHeight);
      list.forEach((edge, at) => ports.set(`${edge.from}\0${edge.to}`, ys[at] ?? centre(id)));
    }
  };
  assign(outPorts, 'from', 'to');
  assign(inPorts, 'to', 'from');

  // Channels. A line that skips layers needs a y it can cross them at without passing
  // through a box. Its own port height is best (one bend instead of two), then the target's,
  // then the clear band nearest the middle — and never a stretch another line already runs
  // along, or two lines merge into one.
  const runs: { y: number; x0: number; x1: number }[] = [];
  // When every band between the boxes is full, a line runs below them all, each in a lane
  // of its own: reusing one fixed y would merge every such line into one.
  const overflow = (taken: (y: number) => boolean) => {
    let y = layout.height + options.rowGap / 2;
    while (taken(y)) y += LANE;
    return y;
  };
  const routes: Route[] = forward.map((edge) => {
    const key = `${edge.from}\0${edge.to}`;
    const from = placed.get(edge.from)!;
    const to = placed.get(edge.to)!;
    const source = { x: from.x + nodeWidth, y: outPorts.get(key) ?? centre(edge.from) };
    const target = { x: to.x, y: inPorts.get(key) ?? centre(edge.to) };
    const route: Route = { from: edge.from, to: edge.to, source, target, channel: null, trunks: [] };

    const firstGutter = from.layer;
    const lastGutter = to.layer - 1;
    if (lastGutter === firstGutter) {
      if (Math.abs(source.y - target.y) > 0.5)
        route.trunks.push({ gutter: firstGutter, top: source.y, bottom: target.y, x: 0 });
      return route;
    }

    const skipped = new Set<number>();
    for (let layer = from.layer + 1; layer < to.layer; layer += 1) skipped.add(layer);
    const bands = freeBands([...placed.values()], skipped, nodeHeight, layout.height, options.rowGap);
    const x0 = gutterLeft(firstGutter);
    const x1 = gutterLeft(lastGutter) + options.columnGap;
    const taken = (y: number) =>
      runs.some((run) => Math.abs(run.y - y) < LANE && overlaps(run.x0, run.x1, x0, x1));
    const clear = (y: number) => bands.some(([top, bottom]) => y >= top && y <= bottom);

    const middle = (source.y + target.y) / 2;
    const candidates = [source.y, target.y];
    for (const [top, bottom] of bands) {
      const mid = (top + bottom) / 2;
      for (let lane = 0; lane * LANE <= (bottom - top) / 2; lane += 1) {
        candidates.push(mid - lane * LANE, mid + lane * LANE);
      }
    }
    const channel =
      candidates.slice(0, 2).find((y) => clear(y) && !taken(y)) ??
      candidates
        .slice(2)
        .filter((y) => clear(y) && !taken(y))
        .sort((a, b) => Math.abs(a - middle) - Math.abs(b - middle))[0] ??
      overflow(taken);

    runs.push({ y: channel, x0, x1 });
    route.channel = channel;
    if (Math.abs(source.y - channel) > 0.5)
      route.trunks.push({ gutter: firstGutter, top: source.y, bottom: channel, x: 0 });
    if (Math.abs(channel - target.y) > 0.5)
      route.trunks.push({ gutter: lastGutter, top: channel, bottom: target.y, x: 0 });
    return route;
  });

  // Lanes. Trunks in one gutter whose vertical runs overlap would draw as one line, so each
  // gets its own lane, and the lanes are spread either side of the gutter's centre.
  const byGutter = new Map<number, Trunk[]>();
  for (const route of routes)
    for (const trunk of route.trunks) {
      const list = byGutter.get(trunk.gutter) ?? [];
      list.push(trunk);
      byGutter.set(trunk.gutter, list);
    }
  for (const [gutter, trunks] of byGutter) {
    const span = (trunk: Trunk) =>
      [Math.min(trunk.top, trunk.bottom), Math.max(trunk.top, trunk.bottom)] as const;
    trunks.sort((a, b) => span(a)[0] - span(b)[0] || span(a)[1] - span(b)[1]);
    const lanes: (readonly [number, number])[][] = [];
    const laneOf = new Map<Trunk, number>();
    for (const trunk of trunks) {
      const [top, bottom] = span(trunk);
      // A pixel of padding, so two runs that only touch end to end still get two lanes.
      let lane = lanes.findIndex((held) =>
        held.every(([heldTop, heldBottom]) => !overlaps(top - 1, bottom + 1, heldTop, heldBottom)),
      );
      if (lane === -1) lane = lanes.push([]) - 1;
      lanes[lane]?.push([top, bottom]);
      laneOf.set(trunk, lane);
    }
    const pitch = Math.min(LANE, (options.columnGap - 2 * LANE) / Math.max(1, lanes.length - 1));
    const middle = gutterLeft(gutter) + options.columnGap / 2;
    for (const trunk of trunks) {
      const lane = laneOf.get(trunk) ?? 0;
      trunk.x = middle + (lane - (lanes.length - 1) / 2) * pitch;
    }
  }

  const drawn: RoutedEdge[] = routes.map((route) => {
    const points = [route.source];
    let y = route.source.y;
    for (const trunk of route.trunks) {
      points.push({ x: trunk.x, y }, { x: trunk.x, y: trunk.bottom });
      y = trunk.bottom;
    }
    points.push({ x: route.target.x, y }, route.target);
    const d = points
      .filter((point, at) => {
        const previous = points[at - 1];
        return !previous || Math.abs(previous.x - point.x) > 0.01 || Math.abs(previous.y - point.y) > 0.01;
      })
      .map((point, at) => `${at ? 'L' : 'M'} ${point.x.toFixed(1)} ${point.y.toFixed(1)}`)
      .join(' ');
    return { from: route.from, to: route.to, d, bottom: Math.max(...points.map((point) => point.y)) };
  });

  for (const edge of layout.edges)
    if (edge.back) drawn.push({ from: edge.from, to: edge.to, d: serviceEdgePath(edge), bottom: 0 });
  return drawn;
}
