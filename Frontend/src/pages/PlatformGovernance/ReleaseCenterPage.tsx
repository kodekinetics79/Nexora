import ArtifactStudioPage from './ArtifactStudioPage';

export default function ReleaseCenterPage() {
  return <ArtifactStudioPage
    title="Test & Release Center"
    subtitle="Test suite definitions, release approval and rollback"
    types={['TestSuite', 'ReleaseCandidate']}
  />;
}
