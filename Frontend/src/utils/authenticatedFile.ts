import axiosInstance from '../api/axiosInstance';

const fetchObjectUrl = async (path: string): Promise<string> => {
  const response = await axiosInstance.get(path, { responseType: 'blob' });
  return URL.createObjectURL(response.data);
};

/**
 * An object URL for an authenticated file, with the content type the server sent, for callers
 * that render the document inline (an iframe or image) rather than in a new tab. The caller owns
 * the URL and must revoke it.
 */
export const fetchAuthenticatedObjectUrl = async (path: string): Promise<{ url: string; contentType: string; blob: Blob }> => {
  const response = await axiosInstance.get(path, { responseType: 'blob' });
  const blob = response.data as Blob;
  return { url: URL.createObjectURL(blob), contentType: blob.type || String(response.headers?.['content-type'] ?? ''), blob };
};

export const downloadAuthenticatedFile = async (path: string, fileName: string): Promise<void> => {
  const url = await fetchObjectUrl(path);
  try {
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
  } finally {
    URL.revokeObjectURL(url);
  }
};

export const openAuthenticatedFile = async (path: string): Promise<void> => {
  // Open synchronously inside the click event so popup blockers do not reject
  // the window while the authenticated request is in flight.
  const opened = window.open('about:blank', '_blank');
  if (!opened)
    throw new Error('The browser blocked the document window.');

  opened.opener = null;
  let url: string;
  try {
    url = await fetchObjectUrl(path);
  } catch (error) {
    opened.close();
    throw error;
  }
  opened.location.href = url;

  // Keep the object URL alive long enough for the new tab to consume it.
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
};
