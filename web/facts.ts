export interface Check {
  name: string;
  state: string;
  url: string;
}
export interface Related {
  repo: string;
  number: number;
  title: string;
  state: string;
  kind: string;
}
export interface StackMember {
  number: number;
  title: string;
  state: string;
  position: number;
}
export interface Stack {
  number: number;
  size: number;
  position: number;
  base: string;
  members: StackMember[];
}
export interface Facts {
  author: string;
  viewer_authored: boolean;
  assignees: string[];
  labels: string[];
  milestone: string;
  comments: number;
  review_decision: string;
  review_requested: boolean;
  reviewers: string[];
  draft: boolean;
  mergeable: string;
  base: string;
  head: string;
  cross_repository: boolean;
  checks_state: string;
  checks: Check[];
  checks_total: number | null;
  unresolved_threads: number;
  threads_total: number;
  state_reason: string;
  related: Related[];
  stack: Stack | null;
  partial: string[];
}
