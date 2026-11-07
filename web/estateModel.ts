export interface EstateProject {
  id: string;
  name: string;
  description: string;
  domain: string;
  stack: string;
  enabled: boolean;
  scanned: boolean;
  manifests: number;
  packages: string[];
  warnings: string[];
}
export interface Dependency {
  source: string;
  target: string;
  registry: string;
  package: string;
  requirement: string;
  scope: string;
  evidence: string;
}
export interface Component {
  id: string;
  members: string[];
  cyclic: boolean;
}
export interface EstateEdge {
  source: string;
  target: string;
}
export interface EstateGraph {
  projects: EstateProject[];
  dependencies: Dependency[];
  components: Component[];
  edges: EstateEdge[];
  scanned_at: string;
  warnings: string[];
}
