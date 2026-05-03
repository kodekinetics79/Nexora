import { enqueueSnackbar } from 'notistack';

export const handleApiError = (error: any) => {
  if (error.response) {
    const { status, data } = error.response;

    if (status === 400 && data.errors) {
      // Handle validation errors
      Object.entries(data.errors).forEach(([field, messages]: [string, any]) => {
        messages.forEach((message: string) => {
          enqueueSnackbar(`${field}: ${message}`, { variant: 'error' });
        });
      });
    } else if (data.message) {
      enqueueSnackbar(data.message, { variant: 'error' });
    } else {
      enqueueSnackbar('An unexpected error occurred.', { variant: 'error' });
    }
  } else if (error.request) {
    enqueueSnackbar('No response from server. Please check your connection.', { variant: 'error' });
  } else {
    enqueueSnackbar(error.message, { variant: 'error' });
  }
};
