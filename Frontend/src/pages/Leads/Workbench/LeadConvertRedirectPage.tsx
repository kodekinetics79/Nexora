import React from 'react';
import { Navigate, useParams } from 'react-router-dom';

/** Compatibility shim for bookmarks and notifications that still use `/convert`. */
const LeadConvertRedirectPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  return <Navigate to={`/procurement/leads/${id}/workbench`} replace />;
};

export default LeadConvertRedirectPage;
