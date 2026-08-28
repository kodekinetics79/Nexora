import React from 'react';
import { TextField, InputAdornment, IconButton, Box } from '@mui/material';
import SearchIcon from '@mui/icons-material/Search';
import CloseIcon from '@mui/icons-material/Close';
import { useTranslation } from 'react-i18next';

interface SearchFieldProps {
  value: string;
  onChange: (value: string) => void;
  placeholder?: string;
  width?: string | number;
  onClear?: () => void;
}

const SearchField: React.FC<SearchFieldProps> = ({ 
  value, 
  onChange, 
  placeholder, 
  width = 320,
  onClear 
}) => {
  const { t } = useTranslation();

  return (
    <Box sx={{ position: 'relative', width }}>
      <TextField
        fullWidth
        size="small"
        placeholder={placeholder || t('search_placeholder')}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        sx={{
          '& .MuiOutlinedInput-root': {
            borderRadius: 1.5,
            backgroundColor: (theme) => theme.palette.mode === 'dark' ? 'rgba(255, 255, 255, 0.03)' : 'rgba(0, 0, 0, 0.02)',
            transition: 'all 0.2s ease',
            '&:hover': {
              backgroundColor: (theme) => theme.palette.mode === 'dark' ? 'rgba(255, 255, 255, 0.05)' : 'rgba(0, 0, 0, 0.04)',
            },
            '&.Mui-focused': {
              backgroundColor: 'background.paper',
              boxShadow: (theme) => `0 0 0 4px ${theme.palette.primary.main}15`,
            }
          }
        }}
        slotProps={{
          input: {
            startAdornment: (
              <InputAdornment position="start">
                <SearchIcon fontSize="small" sx={{ color: 'text.secondary', opacity: 0.7 }} />
              </InputAdornment>
            ),
            endAdornment: value ? (
              <InputAdornment position="end">
                <IconButton 
                  size="small" 
                  aria-label="Clear search"
                  onClick={() => {
                    onChange('');
                    if (onClear) onClear();
                  }}
                  sx={{ p: 0.5 }}
                >
                  <CloseIcon sx={{ fontSize: 14 }} />
                </IconButton>
              </InputAdornment>
            ) : undefined,
          },
          htmlInput: {
            'aria-label': placeholder || t('search_placeholder'),
          },
        }}
      />
    </Box>
  );
};

export default SearchField;
