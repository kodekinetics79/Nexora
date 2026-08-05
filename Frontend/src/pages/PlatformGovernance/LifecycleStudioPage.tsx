import ArtifactStudioPage from './ArtifactStudioPage';

export default function LifecycleStudioPage() {
  return <ArtifactStudioPage
    title="Model, Rule & Dataset Lifecycle"
    subtitle="Governed evaluation, promotion, provenance and rollback"
    types={['Model', 'Rule', 'Dataset']}
  />;
}
