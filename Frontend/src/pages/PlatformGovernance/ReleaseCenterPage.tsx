import ArtifactStudioPage from './ArtifactStudioPage';

export default function ReleaseCenterPage() {
  return <ArtifactStudioPage
    title="Test, Simulation & Release Center"
    subtitle="Deterministic evaluation evidence, release approval and rollback"
    types={['TestSuite', 'ReleaseCandidate']}
  />;
}
