import React from 'react';
import { Box, Typography, useTheme } from '@mui/material';
import BrandMark from './BrandMark';

interface BrandingProps {
  fontSize?: number;
  logoSize?: number;
  showText?: boolean;
  showTagline?: boolean;
}

const Branding: React.FC<BrandingProps> = ({
  fontSize = 24,
  logoSize = 40,
  showText = true,
  showTagline = true,
}) => {
  const theme = useTheme();

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5 }}>
      <BrandMark size={logoSize} face={theme.palette.primary.main} title={showText ? '' : 'Nexora'} />
      {showText && (
        <Box sx={{ display: 'flex', flexDirection: 'column', lineHeight: 1.1 }}>
          <Typography
            sx={{
              fontSize: fontSize,
              fontWeight: 900,
              color: 'text.primary',
              fontFamily: '"Cambay", "Source Sans 3", sans-serif',
              letterSpacing: '-1px',
              whiteSpace: 'nowrap',
              // Letterpress: a hairline of light under the wordmark in light mode only.
              textShadow: theme.palette.mode === 'dark' ? 'none' : '0 1px 0 rgba(255,255,255,0.7)',
            }}
          >
            NEXORA
          </Typography>
          {showTagline && (
            <Typography
              sx={{
                fontSize: Math.max(9, Math.round(fontSize * 0.35)),
                fontWeight: 700,
                color: 'primary.main',
                letterSpacing: '0.1em',
                textTransform: 'uppercase',
                whiteSpace: 'nowrap',
                opacity: 0.8
              }}
            >
              The Intelligence Platform
            </Typography>
          )}
        </Box>
      )}
    </Box>
  );
};

export default Branding;
