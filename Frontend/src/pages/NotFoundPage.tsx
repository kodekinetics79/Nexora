import { Box, Button, Stack, Typography } from '@mui/material';
import { useNavigate } from 'react-router-dom';
import MainLayout from '../components/layout/MainLayout';
import useDocumentTitle from '../hooks/useDocumentTitle';
import { useAuth } from '../context/AuthContext';

/**
 * 404 view. Sets its own title (the route is by definition not in `routeTitles.ts`) and provides
 * the page's `<h1>` — SC 2.4.2 / SC 1.3.1.
 *
 * It used to render outside MainLayout with no links, while telling the reader to "use the
 * navigation menu" — a menu that was not on screen. A signed-in reader now gets the shell (so the
 * menu it names is there) and a button to the one place every day starts; an anonymous reader
 * gets the sign-in door.
 */
const NotFoundPage = () => {
  useDocumentTitle('Page Not Found');
  const { token } = useAuth();
  const navigate = useNavigate();

  const body = (
    <Box component={token ? 'div' : 'main'} id={token ? undefined : 'main-content'} tabIndex={token ? undefined : -1} sx={{ p: 4, maxWidth: 640 }}>
      <Typography variant="h4" component="h1" gutterBottom>
        Page not found
      </Typography>
      <Typography variant="body1" color="text.secondary" sx={{ mb: 3 }}>
        {token
          ? 'The page you requested does not exist. Check the address, or use the navigation menu on the left to continue.'
          : 'The page you requested does not exist. Sign in to continue.'}
      </Typography>
      <Stack direction="row" spacing={1.5}>
        {token ? (
          <>
            <Button variant="contained" onClick={() => navigate('/inbox')} sx={{ fontWeight: 700 }}>Go to Inbox</Button>
            <Button variant="outlined" onClick={() => window.history.back()}>Go back</Button>
          </>
        ) : (
          <Button variant="contained" onClick={() => navigate('/login')} sx={{ fontWeight: 700 }}>Sign in</Button>
        )}
      </Stack>
    </Box>
  );

  return token ? <MainLayout>{body}</MainLayout> : body;
};

export default NotFoundPage;
