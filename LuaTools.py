import tkinter as tk
from tkinter import ttk, messagebox, scrolledtext, filedialog
import tkinter.font as tkfont
import winreg
import os
import sys
import httpx
import re
import json
import threading
import time
import zipfile
import tempfile
import shutil
import subprocess
import psutil
from pathlib import Path
import urllib.request
import urllib.error

# Try to import Windows API for drag and drop
try:
    import ctypes
    from ctypes import wintypes
    from ctypes.wintypes import DWORD, HWND, UINT, WPARAM, LPARAM, BOOL, HANDLE, LPWSTR, LPCWSTR
    from ctypes.wintypes import RECT, POINT
    
    # Windows constants
    WM_DROPFILES = 0x0233
    CF_HDROP = 15
    
    # Windows API functions
    user32 = ctypes.windll.user32
    shell32 = ctypes.windll.shell32
    
    # Function signatures
    DragQueryFileW = shell32.DragQueryFileW
    DragQueryFileW.argtypes = [HANDLE, UINT, LPWSTR, UINT]
    DragQueryFileW.restype = UINT
    
    DragFinish = shell32.DragFinish
    DragFinish.argtypes = [HANDLE]
    
    SetWindowLongPtrW = user32.SetWindowLongPtrW
    SetWindowLongPtrW.argtypes = [HWND, ctypes.c_int, ctypes.c_longlong]
    SetWindowLongPtrW.restype = ctypes.c_longlong
    
    GetWindowLongPtrW = user32.GetWindowLongPtrW
    GetWindowLongPtrW.argtypes = [HWND, ctypes.c_int]
    GetWindowLongPtrW.restype = ctypes.c_longlong
    
    # Constants
    GWLP_WNDPROC = -4
    GWL_EXSTYLE = -20
    WS_EX_ACCEPTFILES = 0x00000010
    
    # Enable drag and drop
    DND_AVAILABLE = True
except ImportError:
    DND_AVAILABLE = False
    print("Windows API not available, drag and drop disabled")

# Try to import archive libraries
try:
    import rarfile
except ImportError:
    rarfile = None

try:
    import py7zr
except ImportError:
    py7zr = None

class SteamStyleApp:
    def __init__(self, root):
        self.root = root
        self.root.title("LuaTools - by melly")
        self.root.geometry("900x700")
        self.root.configure(bg='#0f1419')  # Modern deep dark blue-black
        
        # Set window icon
        try:
            # Try to set the icon from the same directory as the script/executable
            if getattr(sys, 'frozen', False):
                # Running as executable
                icon_path = os.path.join(os.path.dirname(sys.executable), 'icon.ico')
            else:
                # Running as script
                icon_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'icon.ico')
            
            if os.path.exists(icon_path):
                self.root.iconbitmap(icon_path)
        except Exception as e:
            print(f"Could not set window icon: {e}")
        
        # Hide window until it's centered
        self.root.withdraw()
        
        # Center the window on screen
        self.center_window()
        
        # Show the window after centering
        self.root.deiconify()
        
        # Store original window procedure for drag and drop
        self.original_wndproc = None
        
        # Modern color scheme with better contrast and visual appeal
        self.colors = {
            'bg': '#0f1419',  # Deep dark blue-black
            'secondary_bg': '#1a2332',  # Slightly lighter dark blue
            'card_bg': '#232b38',  # Card background
            'accent': '#4f46e5',  # Modern indigo
            'accent_hover': '#6366f1',  # Lighter indigo for hover
            'text': '#f8fafc',  # Almost white text
            'text_secondary': '#cbd5e1',  # Secondary text
            'text_muted': '#94a3b8',  # Muted text
            'button_bg': '#4f46e5',  # Indigo button
            'button_hover': '#6366f1',  # Lighter indigo for hover
            'button_secondary': '#475569',  # Secondary button
            'button_secondary_hover': '#64748b',  # Secondary button hover
            'success': '#10b981',  # Modern green
            'success_hover': '#059669',  # Darker green for hover
            'error': '#ef4444',  # Modern red
            'error_hover': '#dc2626',  # Darker red for hover
            'warning': '#f59e0b',  # Modern amber
            'warning_hover': '#d97706',  # Darker amber for hover
            'border': '#334155',  # Border color
            'border_light': '#475569',  # Light border
            'shadow': '#00000020',  # Shadow color
        }
        
        # Configure modern window styling after colors are defined
        # Removed problematic option_add calls for better compatibility
        
        # Create named fonts to avoid parsing issues
        try:
            self.font_title = tkfont.Font(family="Arial", size=24, weight="bold")
            self.font_subtitle = tkfont.Font(family="Arial", size=14)
            self.font_button = tkfont.Font(family="Arial", size=12)
            self.font_button_large = tkfont.Font(family="Arial", size=20, weight="bold")
            self.font_status = tkfont.Font(family="Arial", size=14, weight="bold")
        except Exception:
            # Fallback if font creation fails
            self.font_title = ("Arial", 24)
            self.font_subtitle = ("Arial", 14)
            self.font_button = ("Arial", 12)
            self.font_button_large = ("Arial", 20)
            self.font_status = ("Arial", 14)
        
        # Force Tk default named fonts to a safe family
        try:
            for name in ["TkDefaultFont", "TkTextFont", "TkHeadingFont", "TkMenuFont", "TkCaptionFont", "TkSmallCaptionFont", "TkTooltipFont", "TkIconFont"]:
                try:
                    tkfont.nametofont(name).configure(family="Arial")
                except Exception:
                    pass
        except Exception:
            pass
        
        # Settings file path - handle both script and executable modes
        if getattr(sys, 'frozen', False):
            # Running as executable (PyInstaller)
            application_path = os.path.dirname(sys.executable)
        else:
            # Running as script
            application_path = os.path.dirname(os.path.abspath(__file__))
        
        self.settings_file = os.path.join(application_path, 'melly-settings.json')
        
        # Load settings
        self.settings = self.load_settings()
        
        # Enable drag and drop if available
        if DND_AVAILABLE:
            self.enable_drag_drop()
        
        # Add method for creating modern rounded buttons
        self.create_modern_button = self.create_modern_button
        self.create_modern_frame = self.create_modern_frame
        
        self.setup_ui()
        
        # Initialize download queue
        self.download_queue = []
        self.completed_downloads = []
        self.failed_downloads = []
        self.current_download = None
        self.download_manager_frame = None
        
        # Track queued games for persistent state
        self.queued_games = set()  # Set of app_ids that are queued or downloading
        
        # Multi-threaded download management
        self.active_downloads = {}  # Dict of {app_id: download_item} for currently downloading items
        self.download_threads = {}  # Dict of {app_id: thread} for active download threads
        self.current_batch_completed = []
        self.current_batch_failed = []
        
        # Persistent HTTP client for downloads (keeps connections warm, supports HTTP/2)
        try:
            self.http_client = httpx.Client(
                http2=True,
                follow_redirects=True,
                headers={"User-Agent": "Mozilla/5.0"}
            )
        except Exception:
            self.http_client = None
        
    def center_window(self):
        """Center the window on the screen"""
        self.root.update_idletasks()
        width = self.root.winfo_width()
        height = self.root.winfo_height()
        x = (self.root.winfo_screenwidth() // 2) - (width // 2)
        y = (self.root.winfo_screenheight() // 2) - (height // 2)
        self.root.geometry(f"{width}x{height}+{x}+{y}")
        
    def center_popup(self, window):
        """Center a popup window on the screen"""
        window.update_idletasks()
        width = window.winfo_width()
        height = window.winfo_height()
        x = (window.winfo_screenwidth() // 2) - (width // 2)
        y = (window.winfo_screenheight() // 2) - (height // 2)
        window.geometry(f"{width}x{height}+{x}+{y}")
    
    def enable_drag_drop(self):
        """Enable drag and drop for the main window"""
        if not DND_AVAILABLE:
            return
            
        try:
            # Get window handle
            hwnd = self.root.winfo_id()
            
            # Enable file drop style
            ex_style = GetWindowLongPtrW(hwnd, GWL_EXSTYLE)
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex_style | WS_EX_ACCEPTFILES)
            
            # Store original window procedure
            self.original_wndproc = GetWindowLongPtrW(hwnd, GWLP_WNDPROC)
            
            # Set up window procedure for drag and drop
            def wndproc(hwnd, msg, wparam, lparam):
                if msg == WM_DROPFILES:
                    self.handle_drop(wparam)
                    return 0
                # Use a safer way to call the original window procedure
                try:
                    return user32.CallWindowProcW(ctypes.cast(self.original_wndproc, ctypes.c_void_p), hwnd, msg, wparam, lparam)
                except:
                    # Fallback: just return 0 if the call fails
                    return 0
            
            # Create window procedure wrapper
            self.wndproc_wrapper = ctypes.WINFUNCTYPE(ctypes.c_longlong, HWND, UINT, WPARAM, LPARAM)(wndproc)
            
            # Set new window procedure
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, ctypes.cast(self.wndproc_wrapper, ctypes.c_void_p).value)
            
        except Exception as e:
            print(f"Failed to enable drag and drop: {e}")
    
    def handle_drop(self, wparam):
        """Handle file drop event"""
        try:
            # Get number of files dropped
            file_count = DragQueryFileW(wparam, 0xFFFFFFFF, None, 0)
            
            dropped_files = []
            invalid_files = []
            
            print(f"Drag and drop: {file_count} files detected")
            
            for i in range(file_count):
                # Get file path length
                path_len = DragQueryFileW(wparam, i, None, 0)
                
                # Create buffer for file path
                buffer = ctypes.create_unicode_buffer(path_len + 1)
                
                # Get file path
                DragQueryFileW(wparam, i, buffer, path_len + 1)
                
                file_path = buffer.value
                print(f"File {i+1}: {file_path}")
                
                # Validate file type
                if self.is_valid_file_type(file_path):
                    print(f"✓ Valid file: {file_path}")
                    dropped_files.append(file_path)
                else:
                    print(f"✗ Invalid file: {file_path}")
                    invalid_files.append(os.path.basename(file_path))
            
            # Finish drag operation
            DragFinish(wparam)
            
            print(f"Valid files: {len(dropped_files)}, Invalid files: {len(invalid_files)}")
            
            # Show warning for invalid files
            if invalid_files:
                invalid_msg = f"The following files were ignored (unsupported format):\n\n"
                for filename in invalid_files:
                    invalid_msg += f"• {filename}\n"
                invalid_msg += f"\nSupported formats: .lua, .zip, .rar, .7z"
                messagebox.showwarning("Unsupported Files", invalid_msg)
            
            # Process valid files only if we have any
            if dropped_files:
                print(f"Processing {len(dropped_files)} valid files...")
                self.process_files(dropped_files)
            elif invalid_files:
                # We had invalid files but no valid ones
                print("No valid files to process")
                messagebox.showwarning("No Files", "No files were dropped.")
                
        except Exception as e:
            print(f"Error handling drop: {e}")
            import traceback
            traceback.print_exc()
            DragFinish(wparam)
        
    def load_settings(self):
        """Load settings from JSON file"""
        default_settings = {
            'auto_restart_steam': False,
            'api_timeout': 10,
            'max_download_threads': 3,  # New setting for concurrent downloads
            'theme': 'steam_dark',
            # Legacy single API settings (for backward compatibility)
            'manifest_download_url': '',
            'manifest_good_status_code': 200,
            'manifest_unavailable_status_code': 404,
            # New modular API system
            'api_list': [],  # Start with no default APIs
            'api_request_timeout': 15,  # New timeout setting for API requests
            'backup_downloads': False,
            'show_only_installed': False,
            'sort_by': 'smart sorting',
            'search_results_limit': 5,
            'installed_games_shown_limit': 25,
            'show_file_names': False,
            'dont_start_downloads_until_button_pressed': False
        }
        
        try:
            if os.path.exists(self.settings_file):
                with open(self.settings_file, 'r', encoding='utf-8') as f:
                    loaded_settings = json.load(f)
                    
                    # Migration: Convert legacy single API settings to new multi-API format
                    if 'api_list' not in loaded_settings and 'manifest_download_url' in loaded_settings:
                        legacy_url = loaded_settings.get('manifest_download_url', '')
                        legacy_success = loaded_settings.get('manifest_good_status_code', 200)
                        legacy_unavailable = loaded_settings.get('manifest_unavailable_status_code', 404)
                        
                        if legacy_url.strip():
                            # Create API list from legacy settings
                            loaded_settings['api_list'] = [
                                {
                                    'name': 'Migrated API',
                                    'url': legacy_url,
                                    'success_code': legacy_success,
                                    'unavailable_code': legacy_unavailable,
                                    'enabled': True
                                }
                            ]
                        else:
                            # Start with empty API list (no default APIs)
                            loaded_settings['api_list'] = []
                    
                    # Merge with defaults to ensure all settings exist
                    for key, value in default_settings.items():
                        if key not in loaded_settings:
                            loaded_settings[key] = value
                    return loaded_settings
            else:
                # Create default settings file
                self.save_settings(default_settings)
                return default_settings
        except Exception as e:
            print(f"Error loading settings: {e}")
            return default_settings
    
    def save_settings(self, settings=None):
        """Save settings to JSON file"""
        if settings is None:
            settings = self.settings
            
        try:
            with open(self.settings_file, 'w', encoding='utf-8') as f:
                json.dump(settings, f, indent=4, ensure_ascii=False)
        except Exception as e:
            print(f"Error saving settings: {e}")
    
    def update_setting(self, key, value):
        """Update a single setting (does not save automatically)"""
        self.settings[key] = value
        
    def create_checkbox_setting(self, parent, title, setting_key, description):
        """Create a checkbox setting widget"""
        # Container frame
        setting_frame = tk.Frame(parent, bg=self.colors['bg'])
        setting_frame.pack(fill=tk.X, pady=10, padx=20)
        
        # Title and checkbox
        title_frame = tk.Frame(setting_frame, bg=self.colors['bg'])
        title_frame.pack(fill=tk.X)
        
        # Checkbox variable
        var = tk.BooleanVar(value=self.settings.get(setting_key, False))
        
        # Store reference to variable
        if hasattr(self, 'setting_vars'):
            self.setting_vars[setting_key] = var
        
        # Checkbox
        checkbox = tk.Checkbutton(
            title_frame,
            text=title,
            variable=var,
            font=('Segoe UI', 12, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg'],
            selectcolor=self.colors['secondary_bg'],
            activebackground=self.colors['bg'],
            activeforeground=self.colors['text']
        )
        checkbox.pack(side=tk.LEFT)
        
        # Description
        desc_label = tk.Label(
            setting_frame,
            text=description,
            font=('Segoe UI', 10),
            fg=self.colors['text'],
            bg=self.colors['bg'],
            wraplength=500,
            justify=tk.LEFT
        )
        desc_label.pack(anchor=tk.W, pady=(5, 0))
        
        # Separator
        separator = tk.Frame(setting_frame, height=1, bg=self.colors['secondary_bg'])
        separator.pack(fill=tk.X, pady=(10, 0))
        
    def create_spinbox_setting(self, parent, title, setting_key, min_val, max_val, increment, description):
        """Create a spinbox setting widget"""
        # Container frame
        setting_frame = tk.Frame(parent, bg=self.colors['bg'])
        setting_frame.pack(fill=tk.X, pady=10, padx=20)
        
        # Title and spinbox
        title_frame = tk.Frame(setting_frame, bg=self.colors['bg'])
        title_frame.pack(fill=tk.X)
        
        # Title label
        title_label = tk.Label(
            title_frame,
            text=title,
            font=('Segoe UI', 12, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        title_label.pack(side=tk.LEFT)
        
        # Spinbox variable
        var = tk.IntVar(value=self.settings.get(setting_key, 10))
        
        # Store reference to variable
        if hasattr(self, 'setting_vars'):
            self.setting_vars[setting_key] = var
        
        # Spinbox
        spinbox = tk.Spinbox(
            title_frame,
            from_=min_val,
            to=max_val,
            increment=increment,
            textvariable=var,
            font=('Segoe UI', 10),
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            relief=tk.FLAT,
            width=10
        )
        spinbox.pack(side=tk.RIGHT)
        
        # Validation function to only allow numbers
        def validate_numeric_input(P):
            if P == "":  # Allow empty string
                return True
            if P.isdigit():  # Allow only digits
                return True
            return False
        
        # Register validation command
        vcmd = (self.root.register(validate_numeric_input), '%P')
        spinbox.config(validate='key', validatecommand=vcmd)
        
        # Bind events to update the variable (but don't save yet)
        def update_var_on_key_release(e):
            try:
                value = var.get()
                if value == "":  # If empty, reset to default
                    var.set(self.settings.get(setting_key, 10))
            except:
                # If conversion fails, reset to default
                var.set(self.settings.get(setting_key, 10))
        
        def update_var_on_focus_out(e):
            try:
                value = var.get()
                if value == "":  # If empty, reset to default
                    var.set(self.settings.get(setting_key, 10))
            except:
                # If conversion fails, reset to default
                var.set(self.settings.get(setting_key, 10))
        
        spinbox.bind('<KeyRelease>', update_var_on_key_release)
        spinbox.bind('<FocusOut>', update_var_on_focus_out)
        
        # Description
        desc_label = tk.Label(
            setting_frame,
            text=description,
            font=('Segoe UI', 10),
            fg=self.colors['text'],
            bg=self.colors['bg'],
            wraplength=500,
            justify=tk.LEFT
        )
        desc_label.pack(anchor=tk.W, pady=(5, 0))
        
        # Separator
        separator = tk.Frame(setting_frame, height=1, bg=self.colors['secondary_bg'])
        separator.pack(fill=tk.X, pady=(10, 0))
        
    def create_text_setting(self, parent, title, setting_key, description, default_value=""):
        """Create a text input setting widget"""
        # Container frame
        setting_frame = tk.Frame(parent, bg=self.colors['bg'])
        setting_frame.pack(fill=tk.X, pady=10, padx=20)
        
        # Title and text input
        title_frame = tk.Frame(setting_frame, bg=self.colors['bg'])
        title_frame.pack(fill=tk.X)
        
        # Title label
        title_label = tk.Label(
            title_frame,
            text=title,
            font=('Segoe UI', 12, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        title_label.pack(side=tk.LEFT)
        
        # Text variable
        var = tk.StringVar(value=self.settings.get(setting_key, default_value))
        
        # Store reference to variable
        if hasattr(self, 'setting_vars'):
            self.setting_vars[setting_key] = var
        
        # Text entry
        text_entry = tk.Entry(
            title_frame,
            textvariable=var,
            font=('Segoe UI', 10),
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            relief=tk.FLAT,
            width=40
        )
        text_entry.pack(side=tk.RIGHT, padx=(10, 0))
        
        # Description
        desc_label = tk.Label(
            setting_frame,
            text=description,
            font=('Segoe UI', 10),
            fg=self.colors['text'],
            bg=self.colors['bg'],
            wraplength=500,
            justify=tk.LEFT
        )
        desc_label.pack(anchor=tk.W, pady=(5, 0))
        
        # Separator
        separator = tk.Frame(setting_frame, height=1, bg=self.colors['secondary_bg'])
        separator.pack(fill=tk.X, pady=(10, 0))
        
    def create_dropdown_setting(self, parent, title, setting_key, options, description):
        """Create a dropdown setting widget"""
        # Container frame
        setting_frame = tk.Frame(parent, bg=self.colors['bg'])
        setting_frame.pack(fill=tk.X, pady=10, padx=20)
        
        # Title and dropdown
        title_frame = tk.Frame(setting_frame, bg=self.colors['bg'])
        title_frame.pack(fill=tk.X)
        
        # Title label
        title_label = tk.Label(
            title_frame,
            text=title,
            font=('Segoe UI', 12, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        title_label.pack(side=tk.LEFT)
        
        # Dropdown variable
        var = tk.StringVar(value=self.settings.get(setting_key, options[0] if options else ''))
        
        # Store reference to variable
        if hasattr(self, 'setting_vars'):
            self.setting_vars[setting_key] = var
        
        # Dropdown
        dropdown = ttk.Combobox(
            title_frame,
            textvariable=var,
            values=options,
            state='readonly',
            font=('Segoe UI', 10),
            width=25
        )
        dropdown.pack(side=tk.RIGHT)
        
        # Description
        desc_label = tk.Label(
            setting_frame,
            text=description,
            font=('Segoe UI', 10),
            fg=self.colors['text'],
            bg=self.colors['bg'],
            wraplength=500,
            justify=tk.LEFT
        )
        desc_label.pack(anchor=tk.W, pady=(5, 0))
        
        # Separator
        separator = tk.Frame(setting_frame, height=1, bg=self.colors['secondary_bg'])
        separator.pack(fill=tk.X, pady=(10, 0))
        
        return dropdown
    
    def create_modern_button(self, parent, text, command, **kwargs):
        """Create a modern button with rounded corners and hover effects"""
        # Default button styling
        default_style = {
            'font': ("Arial", 10),
            'fg': self.colors['text'],
            'bg': self.colors['button_bg'],
            'activebackground': self.colors['button_hover'],
            'activeforeground': self.colors['text'],
            'relief': tk.FLAT,
            'cursor': 'hand2',
            'bd': 0,
            'padx': 20,
            'pady': 10,
        }
        
        # Update with any custom styling
        default_style.update(kwargs)
        
        # Extract hover colors (not valid Button options)
        hover_bg = default_style.pop('hover_bg', self.colors['button_hover'])
        normal_bg = default_style.get('bg', self.colors['button_bg'])
        
        # Check if scaling effects should be disabled
        disable_scaling = kwargs.pop('disable_scaling', False)
        
        # Whitelist valid tkinter Button options
        allowed_button_keys = {
            'activebackground', 'activeforeground', 'anchor', 'background', 'bg',
            'bd', 'borderwidth', 'cursor', 'disabledforeground', 'fg', 'font',
            'foreground', 'highlightbackground', 'highlightcolor', 'highlightthickness',
            'image', 'justify', 'overrelief', 'padx', 'pady', 'relief', 'repeatdelay',
            'repeatinterval', 'state', 'takefocus', 'text', 'textvariable', 'underline',
            'width', 'height', 'wraplength', 'compound'
        }
        button_kwargs = {k: v for k, v in default_style.items() if k in allowed_button_keys}
        
        # Create the button
        button = tk.Button(parent, text=text, command=command, **button_kwargs)
        
        # Add hover effects
        def on_enter(e):
            button.configure(bg=hover_bg)
        
        def on_leave(e):
            button.configure(bg=normal_bg)
        
        button.bind('<Enter>', on_enter)
        button.bind('<Leave>', on_leave)
        
        # Add subtle shadow effect by configuring highlight colors
        button.configure(
            highlightbackground=normal_bg,
            highlightthickness=1
        )
        
        # Add focus styling for better accessibility
        def on_focus_in(e):
            button.configure(highlightbackground=self.colors['accent'])
        
        def on_focus_out(e):
            button.configure(highlightbackground=default_style.get('bg', self.colors['button_bg']))
        
        button.bind('<FocusIn>', on_focus_in)
        button.bind('<FocusOut>', on_focus_out)
        
        # Add click effect for better user feedback
        def on_click(e):
            button.configure(relief=tk.SUNKEN)
            button.after(100, lambda: button.configure(relief=tk.FLAT))
        
        button.bind('<Button-1>', on_click)
        
        # Add subtle scale effect on hover for premium feel (only if not disabled)
        if not disable_scaling:
            def on_enter_scale(e):
                current_font = default_style.get('font', ("Arial", 10))
                if isinstance(current_font, tuple) and len(current_font) >= 2:
                    button.configure(font=(current_font[0], current_font[1] + 1))
            
            def on_leave_scale(e):
                button.configure(font=default_style.get('font', ("Arial", 10)))
            
            button.bind('<Enter>', on_enter_scale)
            button.bind('<Leave>', on_leave_scale)
        
        return button
    
    def create_modern_frame(self, parent, **kwargs):
        """Create a modern frame with subtle borders and modern styling"""
        default_style = {
            'bg': self.colors['card_bg'],
            'relief': tk.FLAT,
            'bd': 0,
            'highlightbackground': self.colors['border'],
            'highlightthickness': 1,
        }
        default_style.update(kwargs)
        return tk.Frame(parent, **default_style)
    
    def create_modern_entry(self, parent, **kwargs):
        """Create a modern entry field with modern styling"""
        default_style = {
            'font': ("Arial", 10),
            'bg': self.colors['secondary_bg'],
            'fg': self.colors['text'],
            'insertbackground': self.colors['text'],
            'relief': tk.FLAT,
            'bd': 0,
            'highlightbackground': self.colors['border_light'],
            'highlightthickness': 1,
            'padx': 10,
            'pady': 8,
        }
        default_style.update(kwargs)
        return tk.Entry(parent, **default_style)

    def setup_ui(self):
        # Main container with modern styling
        main_frame = tk.Frame(self.root, bg=self.colors['bg'])
        main_frame.pack(fill=tk.BOTH, expand=True, padx=40, pady=40)
        
        # Title with modern typography and better spacing
        title_frame = tk.Frame(main_frame, bg=self.colors['bg'])
        title_frame.pack(fill=tk.X, pady=(0, 40))
        
        title_label = tk.Label(
            title_frame,
            text="LuaTools",
            font=self.font_title,
            fg=self.colors['accent'],
            bg=self.colors['bg']
        )
        title_label.pack()
        
        subtitle_label = tk.Label(
            title_frame,
            text="Click the Blue Button to Patch all your ST Games\nor Drag in a ZIP or .lua file for auto processing!",
            font=self.font_subtitle,
            fg=self.colors['text_secondary'],
            bg=self.colors['bg']
        )
        subtitle_label.pack(pady=(10, 0))
        
        # Main button with modern styling and enhanced visual appeal
        button_frame = tk.Frame(main_frame, bg=self.colors['bg'])
        button_frame.pack(pady=(0, 30))
        
        self.patch_button = self.create_modern_button(
            button_frame,
            text="🚀 PATCH .LUAS!",
            command=self.start_patching,
            font=self.font_button_large,
            bg=self.colors['accent'],
            hover_bg=self.colors['accent_hover'],
            padx=60,
            pady=25
        )
        self.patch_button.pack()
        
        # Drag and drop area with modern card design
        self.drop_frame = self.create_modern_frame(
            main_frame,
            bg=self.colors['card_bg']
        )
        self.drop_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 40))
        
        # Add text label inside the drop frame with better styling
        self.drop_label = tk.Label(
            self.drop_frame,
            text="📁 Drag zips and .luas here or click to select!",
            font=("Arial", 16),
            fg=self.colors['text_secondary'],
            bg=self.colors['card_bg'],
            wraplength=500
        )
        self.drop_label.pack(expand=True, fill=tk.BOTH, padx=40, pady=40)
        
        # Make the entire area clickable and add hover effects
        def drop_enter(e):
            self.drop_frame.configure(bg=self.colors['button_secondary_hover'])
            self.drop_label.configure(bg=self.colors['button_secondary_hover'])
        
        def drop_leave(e):
            self.drop_frame.configure(bg=self.colors['card_bg'])
            self.drop_label.configure(bg=self.colors['card_bg'])
        
        # Bind hover and click events to both frame and label
        self.drop_frame.bind('<Enter>', drop_enter)
        self.drop_frame.bind('<Leave>', drop_leave)
        self.drop_frame.bind('<Button-1>', self.select_files)
        self.drop_frame.configure(cursor="hand2")
        
        self.drop_label.bind('<Enter>', drop_enter)
        self.drop_label.bind('<Leave>', drop_leave)
        self.drop_label.bind('<Button-1>', self.select_files)
        self.drop_label.configure(cursor="hand2")
        
        # Bottom button frame with modern styling and better spacing
        self.bottom_frame = tk.Frame(main_frame, bg=self.colors['bg'])
        self.bottom_frame.pack(fill=tk.X, pady=(30, 0))
        
        # Left button group
        left_button_frame = tk.Frame(self.bottom_frame, bg=self.colors['bg'])
        left_button_frame.pack(side=tk.LEFT)
        
        # Settings button (bottom left) with modern styling
        self.settings_button = self.create_modern_button(
            left_button_frame,
            text="⚙ Settings",
            command=self.open_settings,
            font=self.font_button,
            bg=self.colors['button_secondary'],
            hover_bg=self.colors['button_secondary_hover'],
            padx=20,
            pady=12
        )
        self.settings_button.pack(side=tk.LEFT)
        
        # God Mode button (next to settings) with modern styling
        self.god_mode_button = self.create_modern_button(
            left_button_frame,
            text="🎮 God Mode (Game Manager)",
            command=self.open_god_mode,
            font=self.font_button,
            bg=self.colors['warning'],
            hover_bg=self.colors['warning_hover'],
            fg=self.colors['text'],
            padx=25,
            pady=12
        )
        self.god_mode_button.pack(side=tk.LEFT, padx=(20, 0))
        
        # Import/Export button (next to God Mode) with modern styling - DISABLED
        self.import_export_button = self.create_modern_button(
            left_button_frame,
            text="📤 Import/Export",
            command=self.show_import_export_dev_message,
            font=self.font_button,
            bg='#666666',  # Grayed out
            hover_bg='#666666',  # No hover effect
            fg='#999999',  # Muted text color
            padx=25,
            pady=12
        )
        self.import_export_button.pack(side=tk.LEFT, padx=(20, 0))
        
        # Right button group
        right_button_frame = tk.Frame(self.bottom_frame, bg=self.colors['bg'])
        right_button_frame.pack(side=tk.RIGHT)
        
        # Restart Steam button with modern styling
        self.restart_button = self.create_modern_button(
            right_button_frame,
            text="🔄 Restart Steam",
            command=self.restart_steam,
            font=self.font_button,
            bg=self.colors['button_secondary'],
            hover_bg=self.colors['button_secondary_hover'],
            padx=25,
            pady=12
        )
        self.restart_button.pack()
        

        
        # Progress frame (initially hidden) with modern styling
        self.progress_frame = self.create_modern_frame(main_frame, bg=self.colors['bg'])
        
        # Progress bar with modern styling
        self.progress_var = tk.DoubleVar()
        self.progress_bar = ttk.Progressbar(
            self.progress_frame,
            variable=self.progress_var,
            maximum=100,
            length=600,
            mode='determinate'
        )
        self.progress_bar.pack(pady=(30, 20))
        
        # Status label with modern typography
        self.status_label = tk.Label(
            self.progress_frame,
            text="Ready to patch...",
            font=self.font_status,
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        self.status_label.pack(pady=(0, 20))
        
        # Log text area with modern styling
        self.log_text = scrolledtext.ScrolledText(
            self.progress_frame,
            width=80,
            height=18,
            bg=self.colors['card_bg'],
            fg=self.colors['text'],
            font=('Consolas', 11),
            insertbackground=self.colors['text'],
            relief=tk.FLAT,
            bd=0,
            highlightbackground=self.colors['border_light'],
            highlightthickness=1
        )
        self.log_text.pack(pady=(0, 25), padx=25)
        
        # Cancel button with modern styling
        self.cancel_button = self.create_modern_button(
            self.progress_frame,
            text="❌ Cancel",
            command=self.cancel_operation,
            font=self.font_button,
            bg=self.colors['error'],
            hover_bg=self.colors['error_hover'],
            padx=35,
            pady=15
        )
        self.cancel_button.pack()
        
        # Style configuration with modern progress bar
        style = ttk.Style()
        style.theme_use('clam')
        style.configure(
            'TProgressbar',
            background=self.colors['accent'],
            troughcolor=self.colors['card_bg'],
            borderwidth=0,
            lightcolor=self.colors['accent'],
            darkcolor=self.colors['accent']
        )
        
    def log_message(self, message, color=None):
        """Add a message to the log with optional color"""
        if color is None:
            color = self.colors['text']
        
        self.log_text.insert(tk.END, f"{message}\n")
        self.log_text.see(tk.END)
        self.root.update_idletasks()
        
    def update_status(self, message, progress=None):
        """Update status label and progress bar"""
        self.status_label.config(text=message)
        if progress is not None:
            self.progress_var.set(progress)
        self.root.update_idletasks()
        
    def get_steam_install_path(self):
        """Get Steam installation path from Windows registry"""
        try:
            # Try 64-bit registry first
            key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Valve\Steam")
            steam_path = winreg.QueryValueEx(key, "InstallPath")[0]
            winreg.CloseKey(key)
        except:
            try:
                # Try 32-bit registry
                key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Valve\Steam")
                steam_path = winreg.QueryValueEx(key, "InstallPath")[0]
                winreg.CloseKey(key)
            except:
                return None
        return steam_path
        
    def find_lua_files(self, stplugin_path):
        """Find all .lua files in the stplug-in directory"""
        lua_files = []
        disabled_files = []
        try:
            for file in os.listdir(stplugin_path):
                if file.endswith('.lua'):
                    # Only include files that end exactly with .lua (no additional text before .lua)
                    # This excludes files like "filename.text.lua" and only includes "filename.lua"
                    if file.count('.') == 1:  # Only one dot, which should be the .lua extension
                        # Skip Steamtools.lua file
                        if file.lower() != 'steamtools.lua':
                            lua_files.append(os.path.join(stplugin_path, file))
                elif file.endswith('.lua.disabled'):
                    # Check for disabled files
                    if file.count('.') == 2:  # Two dots: filename.lua.disabled
                        # Skip Steamtools.lua.disabled file
                        if file.lower() != 'steamtools.lua.disabled':
                            disabled_files.append(os.path.join(stplugin_path, file))
        except Exception as e:
            self.log_message(f"Error reading stplug-in directory: {e}", self.colors['error'])
        return lua_files, disabled_files
        
    def extract_app_id(self, file_path):
        """Extract app ID from filename (e.g., 613100.lua -> 613100)"""
        filename = os.path.basename(file_path)
        return filename.replace('.lua', '')
        
    def patch_lua_file(self, file_path):
        """Patch a single .lua file by commenting out setManifestid lines"""
        try:
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()
                
            lines = content.split('\n')
            
            # Check if file contains LUATOOLS: UPDATES DISABLED! line
            if any('-- LUATOOLS: UPDATES DISABLED!' in line for line in lines):
                # If updates are disabled, uncomment any --setManifestid lines
                modified = False
                for i, line in enumerate(lines):
                    if line.strip().startswith('--setManifestid'):
                        lines[i] = line[2:]  # Remove the -- prefix
                        modified = True
                
                if modified:
                    with open(file_path, 'w', encoding='utf-8') as f:
                        f.write('\n'.join(lines))
                    return "updates_disabled_modified"
                return "updates_disabled"
            
            # Check if file contains addappid line
            has_addappid = any('addappid' in line.lower() for line in lines)
            if not has_addappid:
                return "no_addappid"
            
            modified = False
            for i, line in enumerate(lines):
                if line.strip().startswith('setManifestid'):
                    lines[i] = '--' + line
                    modified = True
                    
            if modified:
                with open(file_path, 'w', encoding='utf-8') as f:
                    f.write('\n'.join(lines))
                return True
            return False
            
        except Exception as e:
            self.log_message(f"Error patching {file_path}: {e}", self.colors['error'])
            return False
            
    def get_steam_app_info(self, app_id):
        """Get app information from Steam API"""
        try:
            url = f"https://store.steampowered.com/api/appdetails?appids={app_id}"
            timeout = self.settings.get('api_timeout', 10)
            with httpx.Client(timeout=timeout) as client:
                response = client.get(url)
                response.raise_for_status()
                data = response.json()
                
                if data.get(str(app_id), {}).get('success'):
                    app_data = data[str(app_id)]['data']
                    app_type = app_data.get('type', 'unknown')
                    app_name = app_data.get('name', 'Unknown')
                    return app_name, app_type
                else:
                    return f"Unknown (ID: {app_id})", "unknown"
                    
        except Exception as e:
            self.log_message(f"Error fetching info for app {app_id}: {e}", self.colors['error'])
            return f"Error (ID: {app_id})", "error"
            
    def show_results(self, results, time_taken, invalid_files=None):
        """Show results in a new window"""
        if invalid_files is None:
            invalid_files = []
            
        results_window = tk.Toplevel(self.root)
        results_window.title("Patching Results")
        results_window.geometry("600x500")
        results_window.configure(bg=self.colors['bg'])
        
        # Center the popup window
        self.center_popup(results_window)
        
        # Title
        title_label = tk.Label(
            results_window,
            text="Patching Complete!",
            font=('Segoe UI', 18, 'bold'),
            fg=self.colors['success'],
            bg=self.colors['bg']
        )
        title_label.pack(pady=(20, 10))
        
        # Time taken
        time_label = tk.Label(
            results_window,
            text=f"Time taken: {time_taken:.2f} seconds",
            font=('Segoe UI', 12),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        time_label.pack(pady=(0, 20))
        
        # Results text
        results_text = scrolledtext.ScrolledText(
            results_window,
            width=60,
            height=20,
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            font=('Consolas', 10)
        )
        results_text.pack(padx=20, pady=(0, 20))
        
        # Populate results
        if results:
            results_text.insert(tk.END, "Modified files:\n\n")
            for app_id, app_name in results:
                results_text.insert(tk.END, f"App ID: {app_id}\n")
                results_text.insert(tk.END, f"Name: {app_name}\n")
                results_text.insert(tk.END, "-" * 40 + "\n")
        else:
            results_text.insert(tk.END, "No files were modified.\n")
        
        # Show invalid files if any
        if invalid_files:
            results_text.insert(tk.END, f"\nSkipped files (no addappid line found):\n\n")
            for app_id in invalid_files:
                results_text.insert(tk.END, f"✗ App ID: {app_id}\n")
                results_text.insert(tk.END, "-" * 40 + "\n")
            
        results_text.config(state=tk.DISABLED)
        
        # Close button
        close_button = tk.Button(
            results_window,
            text="Close",
            font=('Segoe UI', 12),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            relief=tk.FLAT,
            padx=30,
            pady=10,
            command=results_window.destroy
        )
        close_button.pack(pady=(0, 20))
        
    def cancel_operation(self):
        """Cancel the current operation"""
        self.cancelled = True
        self.log_message("Operation cancelled by user.", self.colors['error'])
        
    def start_patching(self):
        """Start the patching process in a separate thread"""
        self.cancelled = False
        
        # Hide main UI elements during patching
        self.patch_button.pack_forget()
        self.drop_frame.pack_forget()
        self.bottom_frame.pack_forget()
        
        # Show progress frame
        self.progress_frame.pack()
        
        # Start patching in a separate thread
        thread = threading.Thread(target=self.patch_all_files)
        thread.daemon = True
        thread.start()
        
    def patch_all_files(self):
        """Main patching logic"""
        start_time = time.time()
        results = []
        invalid_files = []  # Track files without addappid
        
        try:
            # Step 1: Get Steam install path
            self.update_status("Getting Steam installation path...", 10)
            self.log_message("Getting Steam installation path from registry...")
            
            steam_path = self.get_steam_install_path()
            if not steam_path:
                self.log_message("Error: Could not find Steam installation path in registry", self.colors['error'])
                messagebox.showerror("Error", "Could not find Steam installation path in registry")
                return
                
            self.log_message(f"Found Steam path: {steam_path}")
            
            # Step 2: Check for steam.exe
            self.update_status("Checking for steam.exe...", 20)
            steam_exe = os.path.join(steam_path, "steam.exe")
            if not os.path.exists(steam_exe):
                self.log_message("Error: steam.exe not found in Steam directory", self.colors['error'])
                messagebox.showerror("Error", "steam.exe not found in Steam directory")
                return
                
            self.log_message("steam.exe found ✓")
            
            # Step 3: Check for config folder
            self.update_status("Checking for config folder...", 30)
            config_path = os.path.join(steam_path, "config")
            if not os.path.exists(config_path):
                self.log_message("Error: config folder not found in Steam directory", self.colors['error'])
                messagebox.showerror("Error", "config folder not found in Steam directory")
                return
                
            self.log_message("config folder found ✓")
            
            # Step 4: Check for stplug-in folder
            self.update_status("Checking for stplug-in folder...", 40)
            stplugin_path = os.path.join(config_path, "stplug-in")
            if not os.path.exists(stplugin_path):
                self.log_message("Error: stplug-in folder not found in config directory", self.colors['error'])
                messagebox.showerror("Error", "stplug-in folder not found in config directory")
                return
                
            self.log_message("stplug-in folder found ✓")
            
            # Step 5: Find .lua files
            self.update_status("Finding .lua files...", 50)
            lua_files, disabled_files = self.find_lua_files(stplugin_path)
            if not lua_files and not disabled_files:
                self.log_message("No .lua files found in stplug-in directory", self.colors['error'])
                messagebox.showwarning("Warning", "No .lua files found in stplug-in directory")
                return
                
            self.log_message(f"Found {len(lua_files)} .lua files and {len(disabled_files)} disabled files")
            
            # Step 6: Patch files
            self.update_status("Patching .lua files...", 60)
            modified_files = []
            
            for i, file_path in enumerate(lua_files):
                if self.cancelled:
                    return
                    
                app_id = self.extract_app_id(file_path)
                self.log_message(f"Processing {app_id}.lua...")
                
                patch_result = self.patch_lua_file(file_path)
                if patch_result == "updates_disabled":
                    self.log_message(f"⏸️ Skipped {app_id}.lua - Updates disabled", self.colors['warning'])
                elif patch_result == "updates_disabled_modified":
                    self.log_message(f"⏸️ Skipped {app_id}.lua - Updates disabled (uncommented setManifestid)", self.colors['warning'])
                elif patch_result == "no_addappid":
                    invalid_files.append(app_id)
                    self.log_message(f"✗ Skipped {app_id}.lua - No addappid line found", self.colors['error'])
                elif patch_result:
                    modified_files.append(app_id)
                    self.log_message(f"✓ Patched {app_id}.lua")
                else:
                    self.log_message(f"- No changes needed for {app_id}.lua")
                    
                progress = 60 + (i + 1) * 20 / len(lua_files)
                self.update_status(f"Patching files... ({i+1}/{len(lua_files)})", progress)
                
            # Step 7: Get Steam API info for modified files
            if modified_files:
                # Check if API timeout is set to 0 (skip API calls)
                if self.settings.get('api_timeout', 10) == 0:
                    self.update_status("Skipping Steam API calls (timeout set to 0)...", 80)
                    self.log_message("Skipping Steam API name lookup (timeout set to 0)")
                    
                    for app_id in modified_files:
                        results.append((app_id, f"App ID: {app_id}"))
                        self.log_message(f"✓ {app_id}: App ID: {app_id}")
                else:
                    self.update_status("Getting app information from Steam API...", 80)
                    self.log_message(f"Fetching information for {len(modified_files)} modified apps...")
                    
                    for app_id in modified_files:
                        if self.cancelled:
                            return
                            
                        app_name, app_type = self.get_steam_app_info(app_id)
                        results.append((app_id, app_name))
                        self.log_message(f"✓ {app_id}: {app_name} ({app_type})")
                    
            # Step 8: Complete
            self.update_status("Patching complete!", 100)
            time_taken = time.time() - start_time
            self.log_message(f"Patching completed in {time_taken:.2f} seconds")
            
            # Show results
            if results:
                # Show detailed results window if files were modified
                self.root.after(0, lambda: self.show_results(results, time_taken, invalid_files))
                
                # Auto-restart Steam if enabled
                if self.settings.get('auto_restart_steam', False):
                    self.root.after(1000, self.restart_steam)  # Wait 1 second then restart
            else:
                # Show simple popup if no files were modified
                self.root.after(0, lambda: messagebox.showinfo("No Changes", "No Patchable .Luas Found"))
            
        except Exception as e:
            self.log_message(f"Unexpected error: {e}", self.colors['error'])
            messagebox.showerror("Error", f"An unexpected error occurred: {e}")
            
        finally:
            # Reset UI
            self.root.after(0, self.reset_ui)
            
    def reset_ui(self):
        """Reset the UI to initial state"""
        # Hide progress frame
        self.progress_frame.pack_forget()
        
        # Reset progress
        self.progress_var.set(0)
        self.status_label.config(text="Ready to patch...")
        
        # Show main UI elements again
        self.patch_button.pack(pady=(0, 20))
        self.drop_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 20))
        self.bottom_frame.pack(fill=tk.X, pady=(0, 10))
        
    def open_settings(self):
        """Open the settings menu"""
        # Hide main UI
        for widget in self.root.winfo_children():
            widget.pack_forget()
            
        # Create settings frame with modern styling
        self.settings_frame = self.create_modern_frame(self.root, bg=self.colors['bg'])
        self.settings_frame.pack(fill=tk.BOTH, expand=True, padx=30, pady=30)
        
        # Settings title with modern typography
        title_frame = tk.Frame(self.settings_frame, bg=self.colors['bg'])
        title_frame.pack(fill=tk.X, pady=(0, 40))
        
        settings_title = tk.Label(
            title_frame,
            text="⚙️ Settings",
            font=("Arial", 28),
            fg=self.colors['accent'],
            bg=self.colors['bg']
        )
        settings_title.pack()
        
        # Version number with modern styling
        version_label = tk.Label(
            title_frame,
            text="v2.3",
            font=("Arial", 14),
            fg=self.colors['text_muted'],
            bg=self.colors['bg']
        )
        version_label.pack(pady=(10, 0))
        
        # Create scrollable settings container with modern styling
        canvas_frame = self.create_modern_frame(self.settings_frame, bg=self.colors['bg'])
        canvas_frame.pack(fill=tk.BOTH, expand=True, padx=25, pady=(0, 25))
        
        # Create canvas with modern styling
        canvas = tk.Canvas(canvas_frame, bg=self.colors['card_bg'], highlightthickness=0, bd=0)
        scrollbar = ttk.Style()
        scrollbar.theme_use('clam')
        scrollbar = ttk.Scrollbar(canvas_frame, orient="vertical", command=canvas.yview)
        
        # Create the scrollable frame with modern styling
        settings_container = tk.Frame(canvas, bg=self.colors['card_bg'])
        
        # Configure the canvas
        canvas.configure(yscrollcommand=scrollbar.set)
        
        # Pack the canvas and scrollbar
        canvas.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        
        # Create a window in the canvas for the settings container
        canvas_window = canvas.create_window((0, 0), window=settings_container, anchor="nw")
        
        # Configure the canvas to expand with the window
        def on_canvas_configure(event):
            canvas.configure(scrollregion=canvas.bbox("all"))
            # Update the width of the settings container to match the canvas
            canvas.itemconfig(canvas_window, width=event.width)
        
        def on_frame_configure(event):
            canvas.configure(scrollregion=canvas.bbox("all"))
        
        canvas.bind('<Configure>', on_canvas_configure)
        settings_container.bind('<Configure>', on_frame_configure)
        
        # Bind mouse wheel to canvas
        def _on_mousewheel(event):
            # Check if mouse is over the settings frame
            x, y = self.root.winfo_pointerxy()
            widget_under_mouse = self.root.winfo_containing(x, y)
            
            # Check if the widget under mouse is within the settings frame
            if widget_under_mouse:
                current_widget = widget_under_mouse
                while current_widget:
                    if current_widget == self.settings_frame:
                        canvas.yview_scroll(int(-1*(event.delta/120)), "units")
                        break
                    current_widget = current_widget.master
        
        # Bind to root window to catch all mouse wheel events
        self.root.bind("<MouseWheel>", _on_mousewheel)
        
        # Clear and recreate setting variables dictionary
        self.setting_vars = {}
        
        # Auto-restart Steam setting
        self.create_checkbox_setting(
            settings_container,
            "Auto-restart Steam after patching",
            "auto_restart_steam",
            "Automatically restart Steam when patching is complete"
        )
        
        # API timeout setting
        self.create_spinbox_setting(
            settings_container,
            "Steam API timeout (seconds)",
            "api_timeout",
            0, 999999, 1,
            "Timeout for Steam API requests. Set to 0 to skip API calls and show only App IDs"
        )
        

        
        # Button container for bottom buttons
        button_frame = tk.Frame(self.settings_frame, bg=self.colors['bg'])
        button_frame.pack(pady=(20, 0))
        
        # Downloader Settings button (bottom left)
        downloader_settings_button = self.create_modern_button(
            button_frame,
            text="📥 Downloader Settings",
            command=self.open_downloader_settings,
            font=("Arial", 12),
            bg=self.colors['button_secondary'],
            hover_bg=self.colors['button_secondary_hover'],
            padx=30,
            pady=12
        )
        downloader_settings_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Save and Exit button (bottom right) with modern styling
        back_button = self.create_modern_button(
            button_frame,
            text="💾 Save and Exit",
            command=self.save_and_exit_settings,
            font=("Arial", 12),
            bg=self.colors['success'],
            hover_bg=self.colors['success_hover'],
            padx=35,
            pady=12
        )
        back_button.pack(side=tk.LEFT)
        
    def open_downloader_settings(self):
        """Open the downloader settings submenu"""
        # Hide main settings UI
        for widget in self.root.winfo_children():
            widget.pack_forget()
            
        # Create downloader settings frame with modern styling
        self.downloader_settings_frame = self.create_modern_frame(self.root, bg=self.colors['bg'])
        self.downloader_settings_frame.pack(fill=tk.BOTH, expand=True, padx=30, pady=30)
        
        # Settings title with modern typography
        title_frame = tk.Frame(self.downloader_settings_frame, bg=self.colors['bg'])
        title_frame.pack(fill=tk.X, pady=(0, 40))
        
        settings_title = tk.Label(
            title_frame,
            text="📥 Downloader Settings",
            font=("Arial", 28),
            fg=self.colors['accent'],
            bg=self.colors['bg']
        )
        settings_title.pack()
        
        # Subtitle with modern styling
        subtitle_label = tk.Label(
            title_frame,
            text="Configure manifest download API and related settings",
            font=("Arial", 14),
            fg=self.colors['text_secondary'],
            bg=self.colors['bg']
        )
        subtitle_label.pack(pady=(15, 0))
        
        # Create scrollable settings container with modern styling
        canvas_frame = self.create_modern_frame(self.downloader_settings_frame, bg=self.colors['bg'])
        canvas_frame.pack(fill=tk.BOTH, expand=True, padx=25, pady=(0, 25))
        
        # Create canvas with modern styling
        canvas = tk.Canvas(canvas_frame, bg=self.colors['card_bg'], highlightthickness=0, bd=0)
        scrollbar = ttk.Scrollbar(canvas_frame, orient="vertical", command=canvas.yview)
        
        # Create the scrollable frame with modern styling
        downloader_container = tk.Frame(canvas, bg=self.colors['card_bg'])
        
        # Configure the canvas
        canvas.configure(yscrollcommand=scrollbar.set)
        
        # Pack the canvas and scrollbar
        canvas.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        
        # Create a window in the canvas for the settings container
        canvas_window = canvas.create_window((0, 0), window=downloader_container, anchor="nw")
        
        # Configure the canvas to expand with the window
        def on_canvas_configure(event):
            canvas.configure(scrollregion=canvas.bbox("all"))
            canvas.itemconfig(canvas_window, width=event.width)
        
        def on_frame_configure(event):
            canvas.configure(scrollregion=canvas.bbox("all"))
        
        canvas.bind('<Configure>', on_canvas_configure)
        downloader_container.bind('<Configure>', on_frame_configure)
        
        # Bind mouse wheel to canvas
        def _on_mousewheel(event):
            x, y = self.root.winfo_pointerxy()
            widget_under_mouse = self.root.winfo_containing(x, y)
            
            if widget_under_mouse:
                current_widget = widget_under_mouse
                while current_widget:
                    if current_widget == self.downloader_settings_frame:
                        canvas.yview_scroll(int(-1*(event.delta/120)), "units")
                        break
                    current_widget = current_widget.master
        
        # Bind to root window to catch all mouse wheel events
        self.root.bind("<MouseWheel>", _on_mousewheel)
        
        # Initialize setting variables if not already done
        if not hasattr(self, 'setting_vars'):
            self.setting_vars = {}
        
        # API Request Timeout setting
        self.create_spinbox_setting(
            downloader_container,
            "API Request Timeout (seconds)",
            "api_request_timeout",
            5, 120, 1,
            "How long to wait for each API to respond before trying the next one"
        )
        
        # Max Download Threads setting
        self.create_spinbox_setting(
            downloader_container,
            "Max Download Threads",
            "max_download_threads",
            1, 10, 1,
            "Maximum number of simultaneous downloads (1-10). Higher values download faster but use more resources"
        )
        
        # API Management Section
        api_section_label = tk.Label(
            downloader_container,
            text="API Management",
            font=('Segoe UI', 14, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        api_section_label.pack(pady=(20, 10))
        
        api_desc_label = tk.Label(
            downloader_container,
            text="APIs are tried in order from top to bottom. Use <appid> as placeholder in URLs.",
            font=('Segoe UI', 10),
            fg=self.colors['text_secondary'],
            bg=self.colors['bg'],
            wraplength=600
        )
        api_desc_label.pack(pady=(0, 10))
        
        # API List Container
        self.api_list_container = tk.Frame(downloader_container, bg=self.colors['bg'])
        self.api_list_container.pack(fill=tk.X, padx=10, pady=(0, 10))
        
        # Buttons for API management
        api_buttons_frame = tk.Frame(downloader_container, bg=self.colors['bg'])
        api_buttons_frame.pack(pady=(0, 20))
        
        add_api_button = tk.Button(
            api_buttons_frame,
            text="Add New API",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=20,
            pady=5,
            cursor='hand2',
            command=self.add_new_api
        )
        add_api_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Load FREE APIs button
        self.load_free_apis_button = tk.Button(
            api_buttons_frame,
            text="Load FREE API's",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=20,
            pady=5,
            cursor='hand2',
            command=self.load_free_apis
        )
        self.load_free_apis_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Load and display existing APIs
        self.refresh_api_list()
        
        # Save and Exit button (centered)
        self.save_exit_button = tk.Button(
            self.downloader_settings_frame,
            text="Save and Exit",
            font=('Segoe UI', 12),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=30,
            pady=10,
            cursor='hand2',
            command=self.save_and_exit_settings
        )
        self.save_exit_button.pack(pady=(20, 0))
        
    def open_import_export(self):
        """Open the Import/Export menu"""
        # Hide main UI
        for widget in self.root.winfo_children():
            widget.pack_forget()
            
        # Create Import/Export frame
        self.import_export_frame = tk.Frame(self.root, bg=self.colors['bg'])
        self.import_export_frame.pack(fill=tk.BOTH, expand=True, padx=30, pady=30)
        
        # Check if we already have cached Steam API data
        if hasattr(self, '_steam_api_cache') and hasattr(self, '_steam_api_cache_timestamp'):
            # Check if cache is less than 1 hour old (3600 seconds)
            if time.time() - self._steam_api_cache_timestamp < 3600:
                print("[IMPORT/EXPORT] Using cached Steam API data")
                # Use cached data to show menu immediately
                self.show_import_export_menu()
                return
        
        # Create loading frame
        self.import_export_loading_frame = tk.Frame(self.import_export_frame, bg=self.colors['bg'])
        self.import_export_loading_frame.pack(fill=tk.BOTH, expand=True)
        
        # Loading message
        self.import_export_loading_label = tk.Label(
            self.import_export_loading_frame,
            text="Loading Steam game data...",
            font=('Segoe UI', 14),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        self.import_export_loading_label.pack(pady=(50, 20))
        
        # Progress bar
        self.import_export_progress_bar = ttk.Progressbar(
            self.import_export_loading_frame,
            mode='indeterminate',
            length=400
        )
        self.import_export_progress_bar.pack(pady=(0, 30))
        self.import_export_progress_bar.start()
        
        # Start loading data in background thread
        self.load_import_export_data()
        
        # Function should end here - all UI elements are created by show_import_export_menu()
        return
        
    def load_import_export_data(self):
        """Load Steam API data for Import/Export menu in background thread"""
        def load_thread():
            try:
                # Call Steam API
                response = httpx.get('https://api.steampowered.com/ISteamApps/GetAppList/v2/')
                response.raise_for_status()
                steam_data = response.json()
                
                # Cache the Steam API data for future use
                self._steam_api_cache = steam_data
                self._steam_api_cache_timestamp = time.time()
                print(f"[IMPORT/EXPORT] Cached Steam API data with {len(steam_data.get('applist', {}).get('apps', []))} apps")
                
                # Update UI on main thread
                self.root.after(0, self.show_import_export_menu)
                
            except httpx.RequestError as e:
                self.root.after(0, lambda: self.show_import_export_error(f"Network error: {str(e)}"))
            except httpx.HTTPStatusError as e:
                self.root.after(0, lambda: self.show_import_export_error(f"HTTP error: {e.response.status_code}"))
            except json.JSONDecodeError as e:
                self.root.after(0, lambda: self.show_import_export_error(f"Invalid JSON response: {str(e)}"))
            except Exception as e:
                self.root.after(0, lambda: self.show_import_export_error(f"Unexpected error: {str(e)}"))
        
        # Start background thread
        threading.Thread(target=load_thread, daemon=True).start()
        
    def show_import_export_menu(self):
        """Show the Import/Export menu after Steam data is loaded"""
        # Hide loading frame
        if hasattr(self, 'import_export_loading_frame'):
            self.import_export_loading_frame.pack_forget()
        
        # Title
        title_label = tk.Label(
            self.import_export_frame,
            text="📤 Import/Export",
            font=('Segoe UI', 24, 'bold'),
            fg=self.colors['accent'],
            bg=self.colors['bg']
        )
        title_label.pack(pady=(20, 10))
        
        # Subtitle
        subtitle_label = tk.Label(
            self.import_export_frame,
            text="Import and Export Lua's",
            font=('Segoe UI', 12),
            fg=self.colors['text_secondary'],
            bg=self.colors['bg']
        )
        subtitle_label.pack(pady=(0, 30))
        
        # Main content frame
        content_frame = tk.Frame(self.import_export_frame, bg=self.colors['bg'])
        content_frame.pack(fill=tk.BOTH, expand=True, padx=30, pady=(0, 30))
        
        # Import section (clickable)
        import_frame = self.create_modern_frame(content_frame, bg=self.colors['card_bg'])
        import_frame.pack(fill=tk.X, pady=(0, 20))
        
        # Make import frame clickable
        import_frame.bind("<Button-1>", lambda e: self.import_section_clicked())
        import_frame.configure(cursor="hand2")
        
        import_title = tk.Label(
            import_frame,
            text="📥 Import",
            font=('Segoe UI', 16, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['card_bg']
        )
        import_title.pack(pady=(20, 10))
        
        import_desc = tk.Label(
            import_frame,
            text="Import configuration files, game lists, or other data",
            font=('Segoe UI', 10),
            fg=self.colors['text_secondary'],
            bg=self.colors['card_bg']
        )
        import_desc.pack(pady=(0, 20))
        
        # Export section (clickable)
        export_frame = self.create_modern_frame(content_frame, bg=self.colors['card_bg'])
        export_frame.pack(fill=tk.X, pady=(0, 20))
        
        # Make export frame clickable
        export_frame.bind("<Button-1>", lambda e: self.export_section_clicked())
        export_frame.configure(cursor="hand2")
        
        export_title = tk.Label(
            export_frame,
            text="📤 Export",
            font=('Segoe UI', 16, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['card_bg']
        )
        export_title.pack(pady=(20, 10))
        
        export_desc = tk.Label(
            export_frame,
            text="Export your current configuration, game lists, or other data",
            font=('Segoe UI', 10),
            fg=self.colors['text_secondary'],
            bg=self.colors['card_bg']
        )
        export_desc.pack(pady=(0, 20))
        
        # Now make all frame elements clickable and highlightable (after both frames are defined)
        def import_enter(e):
            import_frame.configure(bg=self.colors['button_secondary_hover'])
            import_title.configure(bg=self.colors['button_secondary_hover'])
            import_desc.configure(bg=self.colors['button_secondary_hover'])
        
        def import_leave(e):
            import_frame.configure(bg=self.colors['card_bg'])
            import_title.configure(bg=self.colors['card_bg'])
            import_desc.configure(bg=self.colors['card_bg'])
        
        def export_enter(e):
            export_frame.configure(bg=self.colors['button_secondary_hover'])
            export_title.configure(bg=self.colors['button_secondary_hover'])
            export_desc.configure(bg=self.colors['button_secondary_hover'])
        
        def export_leave(e):
            export_frame.configure(bg=self.colors['card_bg'])
            export_title.configure(bg=self.colors['card_bg'])
            export_desc.configure(bg=self.colors['card_bg'])
        
        # Bind hover and click events to import frame elements
        import_frame.bind("<Enter>", import_enter)
        import_frame.bind("<Leave>", import_leave)
        import_title.bind("<Enter>", import_enter)
        import_title.bind("<Leave>", import_leave)
        import_desc.bind("<Enter>", import_enter)
        import_desc.bind("<Leave>", import_leave)
        import_title.bind("<Button-1>", lambda e: self.import_section_clicked())
        import_desc.bind("<Button-1>", lambda e: self.import_section_clicked())
        
        # Bind hover and click events to export frame elements
        export_frame.bind("<Enter>", export_enter)
        export_frame.bind("<Leave>", export_leave)
        export_title.bind("<Enter>", export_enter)
        export_title.bind("<Leave>", export_leave)
        export_desc.bind("<Enter>", export_enter)
        export_desc.bind("<Leave>", export_leave)
        export_title.bind("<Button-1>", lambda e: self.export_section_clicked())
        export_desc.bind("<Button-1>", lambda e: self.export_section_clicked())
        
        # Bottom button frame
        bottom_button_frame = tk.Frame(self.import_export_frame, bg=self.colors['bg'])
        bottom_button_frame.pack(fill=tk.X, padx=30, pady=(0, 20))
        
        # Back button (centered)
        back_button = self.create_modern_button(
            bottom_button_frame,
            text="← Back to Main Menu",
            command=self.back_to_main_from_import_export,
            font=('Segoe UI', 10),
            bg=self.colors['button_secondary'],
            hover_bg=self.colors['button_secondary_hover'],
            padx=25,
            pady=10
        )
        back_button.pack(expand=True)
        
    def show_import_export_error(self, error_message):
        """Show error message in Import/Export interface"""
        # Hide loading frame
        if hasattr(self, 'import_export_loading_frame'):
            self.import_export_loading_frame.pack_forget()
        
        # Create error frame
        error_frame = tk.Frame(self.import_export_frame, bg=self.colors['bg'])
        error_frame.pack(fill=tk.BOTH, expand=True)
        
        # Error icon/message
        error_label = tk.Label(
            error_frame,
            text="❌ Error Loading Data",
            font=('Segoe UI', 18, 'bold'),
            fg='#ff6b6b',
            bg=self.colors['bg']
        )
        error_label.pack(pady=(50, 20))
        
        # Error details
        error_details = tk.Label(
            error_frame,
            text=error_message,
            font=('Segoe UI', 12),
            fg=self.colors['text'],
            bg=self.colors['bg'],
            wraplength=600
        )
        error_details.pack(pady=(0, 30))
        
        # Retry button
        retry_button = tk.Button(
            error_frame,
            text="Retry",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=30,
            pady=10,
            cursor='hand2',
            command=lambda: self.retry_import_export_load(error_frame)
        )
        retry_button.pack(pady=(0, 20))
        
        # Back button
        back_button = tk.Button(
            error_frame,
            text="Back",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=10,
            pady=3,
            cursor='hand2',
            command=self.back_to_main_from_import_export
        )
        back_button.pack()
        
    def retry_import_export_load(self, error_frame):
        """Retry loading Import/Export data"""
        # Remove error frame
        error_frame.destroy()
        
        # Show loading frame again
        if hasattr(self, 'import_export_loading_frame'):
            self.import_export_loading_frame.pack(fill=tk.BOTH, expand=True)
            if hasattr(self, 'import_export_progress_bar'):
                self.import_export_progress_bar.start()
        
        # Start loading again
        self.load_import_export_data()
        
    def refresh_api_list(self):
        """Refresh the display of API list in downloader settings"""
        # Clear existing API widgets
        for widget in self.api_list_container.winfo_children():
            widget.destroy()
        
        api_list = self.settings.get('api_list', [])
        
        for i, api in enumerate(api_list):
            self.create_api_card(api, i)
    
    def create_api_card(self, api, index):
        """Create a UI card for a single API entry"""
        # Main API card frame
        api_card = tk.Frame(
            self.api_list_container,
            bg=self.colors['secondary_bg'],
            relief=tk.RAISED,
            bd=1
        )
        api_card.pack(fill=tk.X, pady=5, padx=5)
        
        # Top row: Enable checkbox, name, and controls
        top_row = tk.Frame(api_card, bg=self.colors['secondary_bg'])
        top_row.pack(fill=tk.X, padx=10, pady=5)
        
        # Enable/disable checkbox
        enabled_var = tk.BooleanVar(value=api.get('enabled', True))
        enabled_check = tk.Checkbutton(
            top_row,
            variable=enabled_var,
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['secondary_bg'],
            selectcolor=self.colors['secondary_bg'],
            command=lambda: self.update_api_enabled(index, enabled_var.get())
        )
        enabled_check.pack(side=tk.LEFT)
        
        # API name entry
        name_label = tk.Label(top_row, text="Name:", bg=self.colors['secondary_bg'], fg=self.colors['text'], font=('Segoe UI', 9))
        name_label.pack(side=tk.LEFT, padx=(5, 0))
        
        name_var = tk.StringVar(value=api.get('name', ''))
        name_entry = tk.Entry(
            top_row,
            textvariable=name_var,
            bg=self.colors['bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            width=20,
            font=('Segoe UI', 9)
        )
        name_entry.pack(side=tk.LEFT, padx=(5, 10))
        name_var.trace('w', lambda *args: self.update_api_name(index, name_var.get()))
        
        # Control buttons frame
        controls_frame = tk.Frame(top_row, bg=self.colors['secondary_bg'])
        controls_frame.pack(side=tk.RIGHT)
        
        # Move up button
        if index > 0:
            up_button = tk.Button(
                controls_frame,
                text="↑",
                font=('Segoe UI', 8),
                bg=self.colors['button_bg'],
                fg=self.colors['text'],
                activebackground=self.colors['button_hover'],
                relief=tk.FLAT,
                width=3,
                command=lambda: self.move_api(index, -1)
            )
            up_button.pack(side=tk.LEFT, padx=1)
        
        # Move down button
        if index < len(self.settings.get('api_list', [])) - 1:
            down_button = tk.Button(
                controls_frame,
                text="↓",
                font=('Segoe UI', 8),
                bg=self.colors['button_bg'],
                fg=self.colors['text'],
                activebackground=self.colors['button_hover'],
                relief=tk.FLAT,
                width=3,
                command=lambda: self.move_api(index, 1)
            )
            down_button.pack(side=tk.LEFT, padx=1)
        
        # Delete button
        delete_button = tk.Button(
            controls_frame,
            text="✕",
            font=('Segoe UI', 8),
            bg=self.colors['error'],
            fg=self.colors['text'],
            activebackground='#ff6666',
            relief=tk.FLAT,
            width=3,
            command=lambda: self.delete_api(index)
        )
        delete_button.pack(side=tk.LEFT, padx=(5, 0))
        
        # URL row
        url_row = tk.Frame(api_card, bg=self.colors['secondary_bg'])
        url_row.pack(fill=tk.X, padx=10, pady=(0, 5))
        
        url_label = tk.Label(url_row, text="URL:", bg=self.colors['secondary_bg'], fg=self.colors['text'], font=('Segoe UI', 9))
        url_label.pack(side=tk.LEFT)
        
        url_var = tk.StringVar(value=api.get('url', ''))
        url_entry = tk.Entry(
            url_row,
            textvariable=url_var,
            bg=self.colors['bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            font=('Segoe UI', 9)
        )
        url_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(5, 0))
        url_var.trace('w', lambda *args: self.update_api_url(index, url_var.get()))
        
        # Status codes row
        codes_row = tk.Frame(api_card, bg=self.colors['secondary_bg'])
        codes_row.pack(fill=tk.X, padx=10, pady=(0, 10))
        
        # Success code
        success_label = tk.Label(codes_row, text="Success:", bg=self.colors['secondary_bg'], fg=self.colors['text'], font=('Segoe UI', 9))
        success_label.pack(side=tk.LEFT)
        
        success_var = tk.IntVar(value=api.get('success_code', 200))
        success_spin = tk.Spinbox(
            codes_row,
            from_=100, to=999,
            textvariable=success_var,
            bg=self.colors['bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            width=5,
            font=('Segoe UI', 9)
        )
        success_spin.pack(side=tk.LEFT, padx=(5, 10))
        success_var.trace('w', lambda *args: self.update_api_success_code(index, success_var.get()))
        
        # Unavailable code
        unavail_label = tk.Label(codes_row, text="Not Found:", bg=self.colors['secondary_bg'], fg=self.colors['text'], font=('Segoe UI', 9))
        unavail_label.pack(side=tk.LEFT)
        
        unavail_var = tk.IntVar(value=api.get('unavailable_code', 404))
        unavail_spin = tk.Spinbox(
            codes_row,
            from_=100, to=999,
            textvariable=unavail_var,
            bg=self.colors['bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            width=5,
            font=('Segoe UI', 9)
        )
        unavail_spin.pack(side=tk.LEFT, padx=(5, 0))
        unavail_var.trace('w', lambda *args: self.update_api_unavailable_code(index, unavail_var.get()))
    
    def add_new_api(self):
        """Add a new API to the list"""
        new_api = {
            'name': 'New API',
            'url': 'https://your-api-endpoint.com/<appid>.zip',
            'success_code': 200,
            'unavailable_code': 404,
            'enabled': True
        }
        
        if 'api_list' not in self.settings:
            self.settings['api_list'] = []
        
        self.settings['api_list'].append(new_api)
        self.refresh_api_list()
    
    def delete_api(self, index):
        """Delete an API from the list"""
        if len(self.settings.get('api_list', [])) > 1:  # Keep at least one API
            self.settings['api_list'].pop(index)
            self.refresh_api_list()
        else:
            messagebox.showwarning("Cannot Delete", "At least one API must remain in the list.")
    
    def move_api(self, index, direction):
        """Move an API up or down in the list"""
        api_list = self.settings.get('api_list', [])
        if 0 <= index < len(api_list) and 0 <= index + direction < len(api_list):
            # Swap APIs
            api_list[index], api_list[index + direction] = api_list[index + direction], api_list[index]
            self.refresh_api_list()
    
    def update_api_enabled(self, index, enabled):
        """Update API enabled status"""
        if index < len(self.settings.get('api_list', [])):
            self.settings['api_list'][index]['enabled'] = enabled
    
    def update_api_name(self, index, name):
        """Update API name"""
        if index < len(self.settings.get('api_list', [])):
            self.settings['api_list'][index]['name'] = name
    
    def update_api_url(self, index, url):
        """Update API URL"""
        if index < len(self.settings.get('api_list', [])):
            self.settings['api_list'][index]['url'] = url
    
    def update_api_success_code(self, index, code):
        """Update API success code"""
        if index < len(self.settings.get('api_list', [])):
            self.settings['api_list'][index]['success_code'] = code
    
    def update_api_unavailable_code(self, index, code):
        """Update API unavailable code"""
        if index < len(self.settings.get('api_list', [])):
            self.settings['api_list'][index]['unavailable_code'] = code
    
    def load_free_apis(self):
        """Load free APIs from GitHub raw link and update settings"""
        import json  # Import json for manual parsing
        
        # Show confirmation dialog
        result = messagebox.askyesno(
            "Load FREE APIs",
            "This will replace your current API settings with the free APIs from the community.\n\n"
            "Your current API settings will be lost. Continue?",
            icon='warning'
        )
        
        if not result:
            return
        
        # Disable both buttons and change save button text
        if hasattr(self, 'save_exit_button'):
            self.save_exit_button.config(
                state='disabled', 
                text="Loading FREE API's",
                bg='gray',  # Make button gray so text is visible
                fg='white'  # White text for better contrast
            )
        if hasattr(self, 'load_free_apis_button'):
            self.load_free_apis_button.config(
                state='disabled',
                bg='gray',  # Make button gray so it's clearly disabled
                fg='white'  # White text for better contrast
            )
        
        def load_thread():
            try:
                # Use persistent HTTP client for speed
                client = self.http_client or httpx.Client(http2=True, follow_redirects=True, headers={"User-Agent": "Mozilla/5.0"})
                
                # Load APIs from GitHub raw link
                url = 'https://raw.githubusercontent.com/madoiscool/lt_api_links/refs/heads/main/load_free_manifest_apis'
                print(f"[DEBUG] Fetching from URL: {url}")
                
                response = client.get(url)
                response.raise_for_status()
                
                # Debug: Show response details
                print(f"[DEBUG] Response status: {response.status_code}")
                print(f"[DEBUG] Response headers: {dict(response.headers)}")
                print(f"[DEBUG] Response content type: {response.headers.get('content-type', 'unknown')}")
                
                # Get raw text first for debugging
                raw_text = response.text
                print(f"[DEBUG] Raw response (first 200 chars): {repr(raw_text[:200])}")
                print(f"[DEBUG] Raw response length: {len(raw_text)}")
                
                # Try to parse JSON
                try:
                    api_data = response.json()
                    print(f"[DEBUG] JSON parsed successfully: {type(api_data)}")
                    print(f"[DEBUG] JSON keys: {list(api_data.keys()) if isinstance(api_data, dict) else 'Not a dict'}")
                except Exception as json_error:
                    print(f"[DEBUG] JSON parse error: {json_error}")
                    print(f"[DEBUG] Full response text: {repr(raw_text)}")
                    
                    # Try to fix common GitHub raw file issues
                    print(f"[DEBUG] Attempting to fix malformed JSON...")
                    
                    # Remove leading/trailing whitespace and newlines
                    cleaned_text = raw_text.strip()
                    
                    # Check if it starts with "api_list" (missing opening brace)
                    if cleaned_text.startswith('"api_list"'):
                        print(f"[DEBUG] Detected missing opening brace, cleaning and wrapping in {{}}")
                        
                        # Remove trailing comma and clean up the end
                        if cleaned_text.endswith(',\n'):
                            cleaned_text = cleaned_text[:-2]  # Remove trailing ",\n"
                        elif cleaned_text.endswith(','):
                            cleaned_text = cleaned_text[:-1]  # Remove trailing ","
                        
                        # Remove any trailing whitespace/newlines
                        cleaned_text = cleaned_text.rstrip()
                        
                        print(f"[DEBUG] Cleaned text: {repr(cleaned_text)}")
                        
                        fixed_json = "{" + cleaned_text + "}"
                        print(f"[DEBUG] Fixed JSON: {repr(fixed_json)}")
                        
                        try:
                            api_data = json.loads(fixed_json)
                            print(f"[DEBUG] Fixed JSON parsed successfully!")
                        except Exception as fix_error:
                            print(f"[DEBUG] Fix attempt failed: {fix_error}")
                            print(f"[DEBUG] Final attempt - trying to manually construct valid JSON...")
                            
                            # Last resort: manually extract the array content
                            try:
                                # Find the start and end of the array
                                start_idx = cleaned_text.find('[')
                                end_idx = cleaned_text.rfind(']')
                                if start_idx != -1 and end_idx != -1:
                                    array_content = cleaned_text[start_idx:end_idx+1]
                                    manual_json = '{"api_list": ' + array_content + '}'
                                    print(f"[DEBUG] Manual JSON: {repr(manual_json)}")
                                    api_data = json.loads(manual_json)
                                    print(f"[DEBUG] Manual JSON parsed successfully!")
                                else:
                                    raise ValueError("Could not find array brackets")
                            except Exception as manual_error:
                                print(f"[DEBUG] Manual JSON also failed: {manual_error}")
                                raise ValueError(f"JSON parsing failed and could not be fixed: {json_error}. Raw response: {raw_text[:100]}...")
                    else:
                        raise ValueError(f"JSON parsing failed: {json_error}. Raw response: {raw_text[:100]}...")
                
                # Validate response structure
                if 'api_list' not in api_data:
                    print(f"[DEBUG] Missing 'api_list' key. Available keys: {list(api_data.keys())}")
                    raise ValueError("Invalid response: 'api_list' key not found")
                
                print(f"[DEBUG] Found {len(api_data['api_list'])} APIs")
                
                # Update settings on main thread
                self.root.after(0, lambda: self.finish_loading_free_apis(api_data['api_list'], self.save_exit_button, self.load_free_apis_button))
                
            except Exception as e:
                error_msg = f"Failed to load FREE APIs: {str(e)}"
                print(f"[ERROR] {error_msg}")
                self.root.after(0, lambda: self.show_free_apis_error(error_msg, self.save_exit_button, self.load_free_apis_button))
        
        # Start loading in background thread
        threading.Thread(target=load_thread, daemon=True).start()
    
    def finish_loading_free_apis(self, api_list, save_exit_button, load_free_apis_button):
        """Finish loading free APIs and update UI"""
        try:
            # Update settings
            self.settings['api_list'] = api_list
            
            # Save settings immediately
            self.save_settings()
            
            # Refresh API list display
            self.refresh_api_list()
            
            # Re-enable both buttons and restore save button text and colors
            if save_exit_button:
                save_exit_button.config(
                    state='normal', 
                    text="Save and Exit",
                    bg=self.colors['button_bg'],  # Restore original background color
                    fg=self.colors['text']        # Restore original text color
                )
            if load_free_apis_button:
                load_free_apis_button.config(
                    state='normal',
                    bg=self.colors['button_bg'],  # Restore original background color
                    fg=self.colors['text']        # Restore original text color
                )
            
            # Show success message
            messagebox.showinfo(
                "FREE APIs Loaded",
                f"Successfully loaded {len(api_list)} FREE APIs!\n\n"
                "Your API settings have been updated and saved."
            )
            
        except Exception as e:
            print(f"[ERROR] Failed to finish loading FREE APIs: {e}")
            self.show_free_apis_error(f"Failed to save APIs: {str(e)}", save_exit_button, load_free_apis_button)
    
    def show_free_apis_error(self, error_msg, save_exit_button, load_free_apis_button):
        """Show error message for free APIs loading"""
        # Re-enable both buttons and restore save button text and colors
        if save_exit_button:
            save_exit_button.config(
                state='normal', 
                text="Save and Exit",
                bg=self.colors['button_bg'],  # Restore original background color
                fg=self.colors['text']        # Restore original text color
            )
        if load_free_apis_button:
            load_free_apis_button.config(
                state='normal',
                bg=self.colors['button_bg'],  # Restore original background color
                fg=self.colors['text']        # Restore original text color
            )
        
        # Show error message
        messagebox.showerror("Error Loading FREE APIs", error_msg)
        
    def open_god_mode(self):
        """Open the God Mode (Game Manager) interface"""
        # Hide main UI
        for widget in self.root.winfo_children():
            widget.pack_forget()
            
        # Create God Mode frame
        self.god_mode_frame = tk.Frame(self.root, bg=self.colors['bg'])
        self.god_mode_frame.pack(fill=tk.BOTH, expand=True, padx=20, pady=20)
        
        # Check if we already have cached Steam API data
        if hasattr(self, '_steam_api_cache') and hasattr(self, '_steam_api_cache_timestamp'):
            # Check if cache is less than 1 hour old (3600 seconds)
            if time.time() - self._steam_api_cache_timestamp < 3600:
                print("[GOD MODE] Using cached Steam API data")
                # Use cached data to show games immediately
                self.show_god_mode_games_from_cache()
                return
        
        # Create loading frame
        self.loading_frame = tk.Frame(self.god_mode_frame, bg=self.colors['bg'])
        self.loading_frame.pack(fill=tk.BOTH, expand=True)
        
        # Loading message
        self.loading_label = tk.Label(
            self.loading_frame,
            text="Loading game data from Steam API...",
            font=('Segoe UI', 14),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        self.loading_label.pack(pady=(50, 20))
        
        # Progress bar
        self.progress_bar = ttk.Progressbar(
            self.loading_frame,
            mode='indeterminate',
            length=400
        )
        self.progress_bar.pack(pady=(0, 30))
        self.progress_bar.start()
        
        # Start loading data in background thread
        self.load_god_mode_data()
        
    def load_god_mode_data(self):
        """Load Steam API data and installed .lua files in background thread"""
        def load_thread():
            try:
                # Call Steam API
                response = httpx.get('https://api.steampowered.com/ISteamApps/GetAppList/v2/')
                response.raise_for_status()
                steam_data = response.json()
                
                # Get Steam installation path
                steam_path = self.get_steam_install_path()
                if not steam_path:
                    self.root.after(0, lambda: self.show_god_mode_error("Could not find Steam installation path"))
                    return
                
                # Find .lua files
                stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
                if not os.path.exists(stplugin_path):
                    self.root.after(0, lambda: self.show_god_mode_error("Could not find stplug-in directory"))
                    return
                
                lua_files, disabled_files = self.find_lua_files(stplugin_path)
                
                # Match .lua files with Steam app data
                game_list = []
                
                # Process active lua files
                for lua_file in lua_files:
                    app_id = self.extract_app_id(lua_file)
                    if app_id:
                        # Find game name from Steam API data
                        game_name = "Unknown Game"
                        for app in steam_data.get('applist', {}).get('apps', []):
                            if str(app.get('appid')) == str(app_id):
                                game_name = app.get('name', 'Unknown Game')
                                break
                        
                        game_list.append({
                            'app_id': app_id,
                            'game_name': game_name,
                            'lua_file': os.path.basename(lua_file),
                            'is_installed': True,
                            'is_disabled': False
                        })
                
                # Process disabled lua files
                for disabled_file in disabled_files:
                    app_id = self.extract_app_id(disabled_file.replace('.disabled', ''))
                    if app_id:
                        # Find game name from Steam API data
                        game_name = "Unknown Game"
                        for app in steam_data.get('applist', {}).get('apps', []):
                            if str(app.get('appid')) == str(app_id):
                                game_name = app.get('name', 'Unknown Game')
                                break
                        
                        game_list.append({
                            'app_id': app_id,
                            'game_name': game_name,
                            'lua_file': os.path.basename(disabled_file),
                            'is_installed': True,
                            'is_disabled': True
                        })
                
                # Sort by game name
                game_list.sort(key=lambda x: x['game_name'].lower())
                
                # Cache the Steam API data for future use
                self._steam_api_cache = steam_data
                self._steam_api_cache_timestamp = time.time()
                print(f"[GOD MODE] Cached Steam API data with {len(steam_data.get('applist', {}).get('apps', []))} apps")
                
                # Update UI on main thread
                self.root.after(0, lambda: self.show_god_mode_games(game_list, steam_data))
                
            except httpx.RequestError as e:
                self.root.after(0, lambda: self.show_god_mode_error(f"Network error: {str(e)}"))
            except httpx.HTTPStatusError as e:
                self.root.after(0, lambda: self.show_god_mode_error(f"HTTP error: {e.response.status_code}"))
            except json.JSONDecodeError as e:
                self.root.after(0, lambda: self.show_god_mode_error(f"Invalid JSON response: {str(e)}"))
            except Exception as e:
                self.root.after(0, lambda: self.show_god_mode_error(f"Unexpected error: {str(e)}"))
        
        # Start background thread
        threading.Thread(target=load_thread, daemon=True).start()
        
    def show_god_mode_error(self, error_message):
        """Show error message in God Mode interface"""
        # Hide loading frame if it exists
        if hasattr(self, 'loading_frame'):
            self.loading_frame.pack_forget()
        
        # Create error frame
        error_frame = tk.Frame(self.god_mode_frame, bg=self.colors['bg'])
        error_frame.pack(fill=tk.BOTH, expand=True)
        
        # Error icon/message
        error_label = tk.Label(
            error_frame,
            text="❌ Error Loading Data",
            font=('Segoe UI', 18, 'bold'),
            fg='#ff6b6b',
            bg=self.colors['bg']
        )
        error_label.pack(pady=(50, 20))
        
        # Error details
        error_details = tk.Label(
            error_frame,
            text=error_message,
            font=('Segoe UI', 12),
            fg=self.colors['text'],
            bg=self.colors['bg'],
            wraplength=600
        )
        error_details.pack(pady=(0, 30))
        
        # Retry button
        retry_button = tk.Button(
            error_frame,
            text="Retry",
            font=('Segoe UI', 12),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=30,
            pady=10,
            cursor='hand2',
            command=lambda: self.retry_god_mode_load(error_frame)
        )
        retry_button.pack(pady=(0, 20))
        
        # Back button
        back_button = tk.Button(
            error_frame,
            text="Back",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=10,
            pady=3,
            cursor='hand2',
            command=self.back_to_main
        )
        back_button.pack()
        
    def retry_god_mode_load(self, error_frame):
        """Retry loading God Mode data"""
        # Remove error frame
        error_frame.destroy()
        
        # Show loading frame again if it exists
        if hasattr(self, 'loading_frame'):
            self.loading_frame.pack(fill=tk.BOTH, expand=True)
        if hasattr(self, 'progress_bar'):
            self.progress_bar.start()
        
        # Start loading again
        self.load_god_mode_data()
        
    def show_god_mode_games_from_cache(self):
        """Show games using cached Steam API data (no loading screen)"""
        print("[GOD MODE] Displaying games from cache")
        
        # Get Steam installation path
        steam_path = self.get_steam_install_path()
        if not steam_path:
            self.show_god_mode_error("Could not find Steam installation path")
            return
        
        # Find .lua files
        stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
        if not os.path.exists(stplugin_path):
            self.show_god_mode_error("Could not find stplug-in directory")
            return
        
        lua_files, disabled_files = self.find_lua_files(stplugin_path)
        
        # Match .lua files with cached Steam app data
        game_list = []
        
        # Process active lua files
        for lua_file in lua_files:
            app_id = self.extract_app_id(lua_file)
            if app_id:
                # Find game name from cached Steam API data
                game_name = "Unknown Game"
                for app in self._steam_api_cache.get('applist', {}).get('apps', []):
                    if str(app.get('appid')) == str(app_id):
                        game_name = app.get('name', 'Unknown Game')
                        break
                
                game_list.append({
                    'app_id': app_id,
                    'game_name': game_name,
                    'lua_file': os.path.basename(lua_file),
                    'is_installed': True,
                    'is_disabled': False
                })
        
        # Process disabled lua files
        for disabled_file in disabled_files:
            app_id = self.extract_app_id(disabled_file.replace('.disabled', ''))
            if app_id:
                # Find game name from cached Steam API data
                game_name = "Unknown Game"
                for app in self._steam_api_cache.get('applist', {}).get('apps', []):
                    if str(app.get('appid')) == str(app_id):
                        game_name = app.get('name', 'Unknown Game')
                        break
                
                game_list.append({
                    'app_id': app_id,
                    'game_name': game_name,
                    'lua_file': os.path.basename(disabled_file),
                    'is_installed': True,
                    'is_disabled': True
                })
        
        # Sort by game name
        game_list.sort(key=lambda x: x['game_name'].lower())
        
        # Store data for refresh functionality
        self.god_mode_game_list = game_list
        
        # Show games immediately
        self.show_god_mode_games(game_list, self._steam_api_cache)
    
    def show_god_mode_games(self, game_list, steam_data):
        """Show the game list in God Mode interface"""
        # Store data for refresh functionality
        self.god_mode_game_list = game_list
        self.god_mode_steam_data = steam_data
        
        # Hide loading frame if it exists
        if hasattr(self, 'loading_frame'):
            self.loading_frame.pack_forget()
        
        # Create main content frame
        content_frame = tk.Frame(self.god_mode_frame, bg=self.colors['bg'])
        content_frame.pack(fill=tk.BOTH, expand=True)
        
        # Header with search bar, stats, and buttons
        header_frame = tk.Frame(content_frame, bg=self.colors['bg'])
        header_frame.pack(fill=tk.X, pady=(0, 20))
        
        # Search bar frame (top left)
        search_frame = tk.Frame(header_frame, bg=self.colors['bg'])
        search_frame.pack(side=tk.LEFT, padx=(0, 20))
        
        # Search label
        search_label = tk.Label(
            search_frame,
            text="Search:",
            font=('Segoe UI', 10),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        search_label.pack(side=tk.LEFT, padx=(0, 5))
        
        # Search entry
        search_var = tk.StringVar()
        search_entry = tk.Entry(
            search_frame,
            textvariable=search_var,
            font=('Segoe UI', 10),
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            relief=tk.FLAT,
            width=20
        )
        search_entry.pack(side=tk.LEFT)
        
        # Create a cache for faster searching (store as instance variables)
        self.steam_search_cache = []
        self.installed_games_dict = {str(game['app_id']): game for game in game_list}
        
        # Pre-process Steam data for faster searching
        for app in steam_data.get('applist', {}).get('apps', []):
            app_id = str(app.get('appid'))
            game_name = app.get('name', 'Unknown Game')
            
            # Check if this game is installed and its status
            installed_game = self.installed_games_dict.get(app_id, {})
            is_installed = app_id in self.installed_games_dict
            is_disabled = installed_game.get('is_disabled', False) if is_installed else False
            
            # Get file modification and creation times for installed games
            file_mod_time = None
            file_creation_time = None
            if is_installed and installed_game.get('lua_file'):
                steam_path = self.get_steam_install_path()
                if steam_path:
                    stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
                    lua_file_path = os.path.join(stplugin_path, installed_game['lua_file'])
                    if os.path.exists(lua_file_path):
                        try:
                            stat_info = os.stat(lua_file_path)
                            file_mod_time = stat_info.st_mtime
                            file_creation_time = stat_info.st_ctime
                        except:
                            pass
            
            # Pre-compute lowercase versions for faster searching
            self.steam_search_cache.append({
                'app_id': app_id,
                'game_name': game_name,
                'game_name_lower': game_name.lower(),
                'app_id_lower': app_id.lower(),
                'is_installed': is_installed,
                'is_disabled': is_disabled,
                'lua_file': installed_game.get('lua_file') if is_installed else None,
                'file_mod_time': file_mod_time,
                'file_creation_time': file_creation_time
            })
        
        # Debouncing variables
        search_after_id = None
        
        # Get search results limit from settings (default to 100)
        max_results = self.settings.get('search_results_limit', 100)
        
        # Stats label
        stats_label = tk.Label(
            header_frame,
            text=f"Found {len(game_list)} installed games",
            font=('Segoe UI', 12),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        stats_label.pack(side=tk.LEFT)
        
        # Button frame for refresh and back buttons
        button_frame = tk.Frame(header_frame, bg=self.colors['bg'])
        button_frame.pack(side=tk.RIGHT)
        
        # Download Manager button (round button with download symbol)
        download_manager_button = tk.Button(
            button_frame,
            text="⬇",  # Download symbol
            font=('Segoe UI', 12),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            width=3,  # Make it round/square
            height=1,
            cursor='hand2',
            command=self.open_download_manager
        )
        download_manager_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Update Disabler button (upload symbol)
        update_disabler_button = tk.Button(
            button_frame,
            text="⬆",  # Upload symbol
            font=('Segoe UI', 12),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            width=3,  # Make it round/square
            height=1,
            cursor='hand2',
            command=self.open_update_disabler
        )
        update_disabler_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Settings button (cog emoji)
        settings_button = tk.Button(
            button_frame,
            text="⚙️",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=8,
            pady=3,
            cursor='hand2',
            command=self.open_game_list_settings
        )
        settings_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Back button - store reference for state updates
        self.god_mode_back_button = tk.Button(
            button_frame,
            text="Back",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=10,
            pady=3,
            cursor='hand2',
            command=self.back_to_main
        )
        self.god_mode_back_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Update back button state based on current queue status
        self.update_god_mode_back_button()
        
        # Refresh button - store reference for state updates
        self.god_mode_refresh_button = tk.Button(
            button_frame,
            text="🔄",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=8,
            pady=3,
            cursor='hand2',
            command=self.refresh_god_mode_data
        )
        self.god_mode_refresh_button.pack(side=tk.LEFT)
        
        # Update button states based on current queue status
        self.update_god_mode_buttons()
        
        # Create scrollable frame for games
        canvas = tk.Canvas(content_frame, bg=self.colors['bg'], highlightthickness=0)
        scrollbar = ttk.Scrollbar(content_frame, orient="vertical", command=canvas.yview)
        scrollable_frame = tk.Frame(canvas, bg=self.colors['bg'])
        
        scrollable_frame.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
        )
        
        # Create window that will expand to fill canvas width
        window_id = canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        
        # Make canvas expand to fill available space
        canvas.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        
        # Bind canvas resize to update scrollable frame width
        def on_canvas_configure(event):
            canvas.itemconfig(window_id, width=event.width)
        
        canvas.bind("<Configure>", on_canvas_configure)
        
        # Bind mouse wheel to the root window for scrolling anywhere in God Mode
        def _on_mousewheel(event):
            # Check if mouse is over the God Mode frame
            x, y = self.root.winfo_pointerxy()
            widget_under_mouse = self.root.winfo_containing(x, y)
            
            # Check if the widget under mouse is within the God Mode frame
            if widget_under_mouse:
                current_widget = widget_under_mouse
                while current_widget:
                    if current_widget == self.god_mode_frame:
                        canvas.yview_scroll(int(-1*(event.delta/120)), "units")
                        break
                    current_widget = current_widget.master
        
        # Bind to root window to catch all mouse wheel events
        self.root.bind("<MouseWheel>", _on_mousewheel)
        
        # Optimized search functionality
        def filter_games(*args):
            nonlocal search_after_id
            
            # Cancel previous search
            if search_after_id:
                self.root.after_cancel(search_after_id)
            
            # Debounce search - wait 300ms after user stops typing
            search_after_id = self.root.after(300, lambda: perform_search())
        
        def perform_search():
            search_term = search_var.get().lower()
            filtered_games = []
            total_results_found = 0  # Track total results before limit
            
            # Get current settings (read fresh from settings to apply immediately)
            show_only_installed = self.settings.get('show_only_installed', False)
            sort_by = self.settings.get('sort_by', 'alphabetical A-Z')
            max_results = self.settings.get('search_results_limit', 100)
            
            # Auto-enable "show only installed" for time-based sorting options
            if sort_by in ["last updated (installed only)", "last installed (installed only)"]:
                show_only_installed = True
            
            if search_term:
                # Use pre-processed cache for faster searching
                for game_data in self.steam_search_cache:
                    # If show_only_installed is enabled, only search within installed games
                    if show_only_installed and not game_data['is_installed']:
                        continue
                        
                    # Check if search term matches game name or app ID
                    if (search_term in game_data['game_name_lower'] or 
                        search_term in game_data['app_id_lower']):
                        
                        total_results_found += 1  # Count total results found
                        
                        # Add ALL matching results to filtered_games (no limit here)
                        filtered_games.append({
                            'app_id': game_data['app_id'],
                            'game_name': game_data['game_name'],
                            'lua_file': game_data['lua_file'],
                            'is_installed': game_data['is_installed'],
                            'is_disabled': game_data['is_disabled'],
                            'file_mod_time': game_data['file_mod_time'],
                            'file_creation_time': game_data['file_creation_time']
                        })
            else:
                # No search term - ALWAYS show ALL installed games (no limit, no matter what the setting is)
                for game_data in self.steam_search_cache:
                    if game_data['is_installed']:
                        filtered_games.append({
                            'app_id': game_data['app_id'],
                            'game_name': game_data['game_name'],
                            'lua_file': game_data['lua_file'],
                            'is_installed': game_data['is_installed'],
                            'is_disabled': game_data['is_disabled'],
                            'file_mod_time': game_data['file_mod_time'],
                            'file_creation_time': game_data['file_creation_time']
                        })
            
            # Apply sorting based on setting (sort ALL results first)
            if sort_by == "smart sorting":
                if search_term:
                    # Smart sorting algorithm for search results
                    def smart_sort_key(game):
                        game_name_lower = game['game_name'].lower()
                        score = 0
                        
                        # Priority 1: Exact match at the beginning (highest priority)
                        if game_name_lower.startswith(search_term):
                            score += 1000
                        
                        # Priority 2: Match at the beginning of any word (after space)
                        elif ' ' + search_term in ' ' + game_name_lower:
                            score += 900
                        
                        # Priority 3: Exact substring match (not at beginning)
                        elif search_term in game_name_lower:
                            score += 800
                        
                        # Priority 4: App ID match
                        elif search_term in game['app_id'].lower():
                            score += 700
                        
                        # Priority 5: Installed games get slight boost
                        if game['is_installed']:
                            score += 50
                        
                        # Priority 6: Alphabetical order as final tiebreaker
                        score -= ord(game_name_lower[0]) if game_name_lower else 0
                        
                        return -score  # Negative for reverse sort (highest score first)
                    
                    filtered_games.sort(key=smart_sort_key)
                else:
                    # When no search term, use alphabetical A-Z for smart sorting
                    filtered_games.sort(key=lambda x: x['game_name'].lower())
            elif sort_by == "alphabetical A-Z":
                filtered_games.sort(key=lambda x: x['game_name'].lower())
            elif sort_by == "alphabetical Z-A":
                filtered_games.sort(key=lambda x: x['game_name'].lower(), reverse=True)
            elif sort_by == "last updated (installed only)":
                # Only sort installed games by modification time
                installed_games = [g for g in filtered_games if g['is_installed'] and g['file_mod_time'] is not None]
                non_installed_games = [g for g in filtered_games if not g['is_installed'] or g['file_mod_time'] is None]
                
                # Sort installed games by modification time (newest first)
                installed_games.sort(key=lambda x: x['file_mod_time'], reverse=True)
                
                # Combine sorted installed games with non-installed games
                filtered_games = installed_games + non_installed_games
            elif sort_by == "last installed (installed only)":
                # Only sort installed games by creation time
                installed_games = [g for g in filtered_games if g['is_installed'] and g['file_creation_time'] is not None]
                non_installed_games = [g for g in filtered_games if not g['is_installed'] or g['file_creation_time'] is None]
                
                # Sort installed games by creation time (newest first)
                installed_games.sort(key=lambda x: x['file_creation_time'], reverse=True)
                
                # Combine sorted installed games with non-installed games
                filtered_games = installed_games + non_installed_games
            
            # Apply the limit AFTER sorting
            if search_term:
                # When searching, apply search results limit
                if len(filtered_games) > max_results:
                    filtered_games = filtered_games[:max_results]
            else:
                # When no search term, apply installed games shown limit
                installed_games_limit = self.settings.get('installed_games_shown_limit', 25)
                if len(filtered_games) > installed_games_limit:
                    filtered_games = filtered_games[:installed_games_limit]
            
            # Update the display with total results information
            update_game_display(filtered_games, total_results_found if search_term else None)
        
        def update_game_display(games_to_show, total_results_found=None):
            # Clear existing games
            for widget in scrollable_frame.winfo_children():
                widget.destroy()
            
            # Show games
            for game in games_to_show:
                self.create_game_card(game, scrollable_frame)
            
            # Update canvas scroll region
            canvas.configure(scrollregion=canvas.bbox("all"))
            
            # Scroll to top when search term is entered
            if search_var.get().strip():
                canvas.yview_moveto(0)
            
            # Update stats based on whether there's a search term
            search_term = search_var.get().strip()
            shown_count = len(games_to_show)
            
            if search_term and total_results_found is not None:
                # When searching, show search results info
                max_results = self.settings.get('search_results_limit', 100)
                stats_text = f"Showing {min(shown_count, max_results)} of {total_results_found} results"
            else:
                # When not searching, show limited installed games info
                installed_count = sum(1 for g in self.steam_search_cache if g['is_installed'])
                installed_games_limit = self.settings.get('installed_games_shown_limit', 25)
                stats_text = f"Showing {min(shown_count, installed_games_limit)}/{installed_count} installed games"
            
            stats_label.config(text=stats_text)
        
        # Bind search entry to filter function
        search_var.trace('w', filter_games)
        
        # Initial display based on settings
        perform_search()
        
        # Store references to search functions for external access
        self.current_search_var = search_var
        self.current_perform_search = perform_search
        self.current_filter_games = filter_games
        self.current_canvas = canvas
        
    def refresh_god_mode_data(self):
        """Refresh the God Mode data by reloading from Steam API"""
        # Check if there are any active downloads
        has_active_downloads = len(self.download_queue) > 0 or self.current_download is not None
        
        if has_active_downloads:
            # Show warning message with option to clear queue
            result = messagebox.askyesno(
                "Downloads in Progress", 
                "Cannot refresh while downloads are in progress.\n\n"
                "Would you like to cancel all downloads and refresh?\n\n"
                "Click 'Yes' to cancel downloads and refresh, or 'No' to keep downloads running."
            )
            
            if result:
                # User chose to cancel downloads and refresh
                self.clear_download_queue()
                print("[REFRESH] Downloads cancelled, proceeding with refresh")
            else:
                # User chose to keep downloads running
                return
        
        # Clear cache to force fresh data load
        if hasattr(self, '_steam_api_cache'):
            delattr(self, '_steam_api_cache')
        if hasattr(self, '_steam_api_cache_timestamp'):
            delattr(self, '_steam_api_cache_timestamp')
        print("[REFRESH] Cleared Steam API cache, will fetch fresh data")
        
        # Clear all existing widgets in god_mode_frame
        for widget in self.god_mode_frame.winfo_children():
            widget.destroy()
        
        # Recreate the loading frame (since it might have been destroyed)
        self.loading_frame = tk.Frame(self.god_mode_frame, bg=self.colors['bg'])
        self.loading_frame.pack(fill=tk.BOTH, expand=True)
        
        # Recreate loading message
        self.loading_label = tk.Label(
            self.loading_frame,
            text="Refreshing game data from Steam API...",
            font=('Segoe UI', 14),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        self.loading_label.pack(pady=(50, 20))
        
        # Recreate progress bar
        self.progress_bar = ttk.Progressbar(
            self.loading_frame,
            mode='indeterminate',
            length=400
        )
        self.progress_bar.pack(pady=(0, 30))
        self.progress_bar.start()
        
        # Start loading again
        self.load_god_mode_data()
        
    def save_and_exit_settings(self):
        """Save all settings and return to main UI"""
        # Collect all current settings from stored variables
        if hasattr(self, 'setting_vars'):
            for setting_key, var in self.setting_vars.items():
                try:
                    self.settings[setting_key] = var.get()
                except Exception as e:
                    print(f"Error getting value for {setting_key}: {e}")
        
        # Filter out placeholder/default APIs before saving
        if 'api_list' in self.settings:
            placeholder_urls = [
                'https://example.com/download?appid=<appid>',
                'https://example.com/<appid>.zip',
                'https://placeholder.com/<appid>',
                'https://your-api-endpoint.com/<appid>.zip'
            ]
            
            # Filter out APIs with placeholder URLs or generic names
            filtered_apis = []
            for api in self.settings['api_list']:
                url = api.get('url', '').strip()
                name = api.get('name', '').strip()
                
                # Skip if it's a placeholder URL or generic name
                if (url in placeholder_urls or 
                    'example.com' in url or 
                    'placeholder.com' in url or
                    name in ['New API', 'Default API', 'Placeholder API']):
                    print(f"[INFO] Skipping placeholder API: {name} - {url}")
                    continue
                
                # Skip if URL is empty or just whitespace
                if not url or url == '<appid>':
                    print(f"[INFO] Skipping API with empty/invalid URL: {name}")
                    continue
                
                filtered_apis.append(api)
            
            # Update the API list with filtered results
            self.settings['api_list'] = filtered_apis
            print(f"[INFO] Filtered API list: {len(filtered_apis)} valid APIs kept")
        
        # Save all current settings
        self.save_settings()
        
        # Clean up mouse wheel binding
        self.root.unbind("<MouseWheel>")
        
        # Clean up settings frames and variables
        if hasattr(self, 'settings_frame'):
            self.settings_frame.destroy()
            delattr(self, 'settings_frame')
            
        if hasattr(self, 'downloader_settings_frame'):
            self.downloader_settings_frame.destroy()
            delattr(self, 'downloader_settings_frame')
        
        # Clear setting variables
        if hasattr(self, 'setting_vars'):
            delattr(self, 'setting_vars')
            
        # Show main UI again
        self.setup_ui()
        
    def back_to_main(self):
        """Return to main UI from settings or God Mode"""
        # Check if there are any active downloads
        has_active_downloads = len(self.download_queue) > 0 or self.current_download is not None
        
        if has_active_downloads:
            # Show warning message
            messagebox.showwarning(
                "Downloads in Progress", 
                "Cannot return to main menu while downloads are in progress.\nPlease wait for all downloads to complete."
            )
            return
        
        # Clean up mouse wheel binding
        self.root.unbind("<MouseWheel>")
        
        # Clean up settings frame and variables
        if hasattr(self, 'settings_frame'):
            self.settings_frame.destroy()
            delattr(self, 'settings_frame')
        
        # Clean up God Mode frame and related attributes
        if hasattr(self, 'god_mode_frame'):
            self.god_mode_frame.destroy()
            delattr(self, 'god_mode_frame')
        
        # Clean up God Mode data
        if hasattr(self, 'god_mode_game_list'):
            delattr(self, 'god_mode_game_list')
        if hasattr(self, 'god_mode_steam_data'):
            delattr(self, 'god_mode_steam_data')
        
        # Clear setting variables
        if hasattr(self, 'setting_vars'):
            delattr(self, 'setting_vars')
            
        # Show main UI again
        self.setup_ui()
        
    def select_files(self, event=None):
        """Open file dialog to select .lua files or zip archives"""
        filetypes = [
            ("Supported files", "*.lua;*.zip;*.rar;*.7z"),
            ("Lua files", "*.lua"),
            ("Archive files", "*.zip;*.rar;*.7z"),
            ("All files", "*.*")
        ]
        
        files = filedialog.askopenfilenames(
            title="Select .lua files or archives",
            filetypes=filetypes
        )
        
        if files:
            self.process_files(files)
    

    
    def process_files(self, file_paths):
        """Process the selected files (lua files or archives)"""
        lua_files = []
        temp_dirs = []  # Keep track of temp directories to clean up later
        
        for file_path in file_paths:
            file_path = file_path.strip('"{}')  # Clean up path
            if not os.path.exists(file_path):
                continue
                
            if file_path.lower().endswith('.lua'):
                # Direct .lua file
                if self.is_valid_lua_filename(os.path.basename(file_path)):
                    lua_files.append(file_path)
            elif file_path.lower().endswith(('.zip', '.rar', '.7z')):
                # Archive file
                result = self.extract_lua_from_archive(file_path)
                if isinstance(result, tuple) and len(result) == 2:
                    archive_lua_files, temp_dir = result
                    lua_files.extend(archive_lua_files)
                    if temp_dir:
                        temp_dirs.append(temp_dir)
                else:
                    # Handle case where extraction failed
                    continue
        
        if lua_files:
            self.process_lua_files(lua_files, temp_dirs)
        else:
            messagebox.showwarning("No valid files", "No valid .lua files found in the selected files.")
    
    def is_valid_file_type(self, file_path):
        """Check if file type is supported for drag and drop"""
        if not os.path.exists(file_path):
            print(f"File does not exist: {file_path}")
            return False
        
        file_lower = file_path.lower()
        print(f"Checking file: {file_path} (lowercase: {file_lower})")
        
        # Check for supported file types
        if file_lower.endswith('.lua'):
            print(f"✓ Valid .lua file: {file_path}")
            return True
        elif file_lower.endswith(('.zip', '.rar', '.7z')):
            print(f"✓ Valid archive file: {file_path}")
            return True
        
        print(f"✗ Unsupported file type: {file_path}")
        return False
    
    def is_valid_lua_filename(self, filename):
        """Check if filename is valid (only numbers + .lua)"""
        if not filename.lower().endswith('.lua'):
            return False
        
        # Remove .lua extension
        name_part = filename[:-4]
        
        # Check if name part contains only digits
        return name_part.isdigit()
    
    def extract_lua_from_archive(self, archive_path):
        """Extract .lua files from archive"""
        lua_files = []
        temp_dir = tempfile.mkdtemp()
        
        try:
            archive_lower = archive_path.lower()
            
            if archive_lower.endswith('.zip'):
                with zipfile.ZipFile(archive_path, 'r') as zip_ref:
                    zip_ref.extractall(temp_dir)
                    
            elif archive_lower.endswith('.rar'):
                if rarfile:
                    with rarfile.RarFile(archive_path, 'r') as rar_ref:
                        rar_ref.extractall(temp_dir)
                else:
                    messagebox.showwarning("RAR Support", "RAR support not available. Install rarfile library.")
                    return []
                    
            elif archive_lower.endswith('.7z'):
                if py7zr:
                    with py7zr.SevenZipFile(archive_path, 'r') as sz_ref:
                        sz_ref.extractall(temp_dir)
                else:
                    messagebox.showwarning("7Z Support", "7Z support not available. Install py7zr library.")
                    return []
            
            # Find all .lua files in extracted directory
            for root, dirs, files in os.walk(temp_dir):
                for file in files:
                    if file.lower().endswith('.lua'):
                        if self.is_valid_lua_filename(file):
                            lua_files.append(os.path.join(root, file))
                        
        except Exception as e:
            messagebox.showerror("Archive Error", f"Error extracting archive: {e}")
            # Clean up temp directory on error
            shutil.rmtree(temp_dir, ignore_errors=True)
            return []
        
        return lua_files, temp_dir  # Return both files and temp directory
    
    def process_lua_files(self, lua_files, temp_dirs=None, show_popup=True):
        """Process the extracted/selected .lua files"""
        if temp_dirs is None:
            temp_dirs = []
            
        # Get Steam stplug-in directory
        steam_path = self.get_steam_install_path()
        if not steam_path:
            messagebox.showerror("Error", "Could not find Steam installation path")
            return
        
        stplugin_path = os.path.join(steam_path, "config", "stplug-in")
        if not os.path.exists(stplugin_path):
            messagebox.showerror("Error", "stplug-in directory not found")
            return
        
        # Process each .lua file
        processed_files = []
        app_ids = []
        invalid_files = []  # Track files without addappid
        
        try:
            for lua_file in lua_files:
                filename = os.path.basename(lua_file)
                app_id = filename[:-4]  # Remove .lua extension
                
                # First, patch the file (add "--" to setManifestid lines)
                patch_result = self.patch_lua_file(lua_file)
                if patch_result == "updates_disabled":
                    print(f"[INFO] Skipped {filename} - Updates disabled")
                    continue  # Skip this file
                elif patch_result == "updates_disabled_modified":
                    print(f"[INFO] Skipped {filename} - Updates disabled (uncommented setManifestid)")
                    continue  # Skip this file
                elif patch_result == "no_addappid":
                    invalid_files.append(app_id)
                    continue  # Skip this file
                elif patch_result:
                    # Copy patched file to stplug-in directory
                    dest_path = os.path.join(stplugin_path, filename)
                    try:
                        # Remove existing file if it exists to avoid copy conflicts
                        if os.path.exists(dest_path):
                            os.remove(dest_path)
                        
                        # Also remove any disabled version of the same file (e.g., 500.lua.disabled)
                        disabled_path = dest_path + ".disabled"
                        if os.path.exists(disabled_path):
                            os.remove(disabled_path)
                            print(f"[INFO] Removed disabled file: {os.path.basename(disabled_path)}")
                        
                        shutil.copy2(lua_file, dest_path)
                        processed_files.append(filename)
                        app_ids.append(app_id)
                    except Exception as e:
                        messagebox.showerror("Error", f"Error copying {filename}: {e}")
                else:
                    # File was patched but no changes were needed
                    # Still copy it to stplug-in directory
                    dest_path = os.path.join(stplugin_path, filename)
                    try:
                        # Remove existing file if it exists to avoid copy conflicts
                        if os.path.exists(dest_path):
                            os.remove(dest_path)
                        
                        # Also remove any disabled version of the same file (e.g., 500.lua.disabled)
                        disabled_path = dest_path + ".disabled"
                        if os.path.exists(disabled_path):
                            os.remove(disabled_path)
                            print(f"[INFO] Removed disabled file: {os.path.basename(disabled_path)}")
                        
                        shutil.copy2(lua_file, dest_path)
                        processed_files.append(filename)
                        app_ids.append(app_id)
                    except Exception as e:
                        messagebox.showerror("Error", f"Error copying {filename}: {e}")
            
            if processed_files:
                # Get app names from Steam API
                self.get_app_names_and_show_results(app_ids, invalid_files, show_popup)
            elif invalid_files:
                # Show error for invalid files
                error_msg = f"The following files were skipped (no addappid line found):\n\n"
                for app_id in invalid_files:
                    error_msg += f"• {app_id}.lua\n"
                messagebox.showwarning("Invalid Files", error_msg)
                
        finally:
            # Clean up temp directories after copying is complete
            for temp_dir in temp_dirs:
                try:
                    shutil.rmtree(temp_dir, ignore_errors=True)
                except:
                    pass
    
    def get_app_names_and_show_results(self, app_ids, invalid_files=None, show_popup=True):
        """Get app names from Steam API and show results"""
        if invalid_files is None:
            invalid_files = []
            
        results = []
        
        # Check if API timeout is set to 0 (skip API calls)
        if self.settings.get('api_timeout', 10) == 0:
            for app_id in app_ids:
                results.append((app_id, f"App ID: {app_id}"))
        else:
            for app_id in app_ids:
                app_name, app_type = self.get_steam_app_info(app_id)
                results.append((app_id, app_name))
        
        # Show results popup only if requested
        if show_popup:
            self.show_added_results(results, invalid_files)
    
    def show_added_results(self, results, invalid_files=None):
        """Show results popup for added files"""
        if invalid_files is None:
            invalid_files = []
            
        results_window = tk.Toplevel(self.root)
        results_window.title("Files Added")
        results_window.geometry("500x400")
        results_window.configure(bg=self.colors['bg'])
        
        # Center the popup window
        self.center_popup(results_window)
        
        # Title
        title_label = tk.Label(
            results_window,
            text="Files Added Successfully!",
            font=('Segoe UI', 18, 'bold'),
            fg=self.colors['success'],
            bg=self.colors['bg']
        )
        title_label.pack(pady=(20, 10))
        
        # Instructions
        instructions_label = tk.Label(
            results_window,
            text="Restart Steam for changes to reflect",
            font=('Segoe UI', 12),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        instructions_label.pack(pady=(0, 20))
        
        # Results text
        results_text = scrolledtext.ScrolledText(
            results_window,
            width=50,
            height=15,
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            font=('Consolas', 10)
        )
        results_text.pack(padx=20, pady=(0, 20))
        
        # Populate results
        if results:
            results_text.insert(tk.END, "Added files:\n\n")
            for app_id, app_name in results:
                results_text.insert(tk.END, f"App ID: {app_id}\n")
                results_text.insert(tk.END, f"Name: {app_name}\n")
                results_text.insert(tk.END, "-" * 40 + "\n")
        
        # Show invalid files if any
        if invalid_files:
            results_text.insert(tk.END, f"\nSkipped files (no addappid line found):\n\n")
            for app_id in invalid_files:
                results_text.insert(tk.END, f"✗ App ID: {app_id}\n")
                results_text.insert(tk.END, "-" * 40 + "\n")
        
        results_text.config(state=tk.DISABLED)
        
        # Close button
        close_button = tk.Button(
            results_window,
            text="Close",
            font=('Segoe UI', 12),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            relief=tk.FLAT,
            padx=30,
            pady=10,
            command=results_window.destroy
        )
        close_button.pack(pady=(0, 20))
    
    def restart_steam(self):
        """Restart Steam manually or if auto-restart is enabled"""
        try:
            # Kill Steam process
            os.system('taskkill /f /im steam.exe 2>nul')
            time.sleep(2)
            
            # Start Steam
            steam_path = self.get_steam_install_path()
            if steam_path:
                steam_exe = os.path.join(steam_path, 'steam.exe')
                if os.path.exists(steam_exe):
                    subprocess.Popen([steam_exe])
                    self.log_message("Steam restarted successfully", self.colors['success'])
                else:
                    self.log_message("Steam executable not found", self.colors['error'])
            else:
                self.log_message("Steam path not found", self.colors['error'])
        except Exception as e:
            self.log_message(f"Error restarting Steam: {e}", self.colors['error'])

    def create_download_directory(self):
        """Create the config directory for downloads if it doesn't exist"""
        try:
            # Get the directory where the .exe is located
            if getattr(sys, 'frozen', False):
                # Running as compiled executable
                exe_dir = os.path.dirname(sys.executable)
            else:
                # Running as script
                exe_dir = os.path.dirname(os.path.abspath(__file__))
            
            # Create config directory
            config_dir = os.path.join(exe_dir, 'config')
            if not os.path.exists(config_dir):
                os.makedirs(config_dir)
            
            return config_dir
        except Exception as e:
            self.log_message(f"Error creating download directory: {e}", self.colors['error'])
            return None

    def download_manifest(self, app_id, game_name):
        """Download manifest for a specific game using the modular API system.
        Tries each enabled API in sequence until one succeeds."""
        try:
            print(f"[DOWNLOAD] Starting download for {game_name} (App ID: {app_id})")
            
            # Get API list and timeout settings
            api_list = self.settings.get('api_list', [])
            enabled_apis = [api for api in api_list if api.get('enabled', True)]
            
            if not enabled_apis:
                print(f"[DOWNLOAD] ERROR: No enabled APIs configured for {app_id}")
                return False, "No enabled APIs configured"
            
            # Get timeout setting
            api_timeout = self.settings.get('api_request_timeout', 15)

            download_dir = self.create_download_directory()
            if not download_dir:
                print(f"[DOWNLOAD] ERROR: Failed to create download directory for {app_id}")
                return False, "Failed to create download directory"

            # Ensure we have a client; fall back to a transient one if needed
            client = self.http_client or httpx.Client(http2=True, follow_redirects=True, headers={"User-Agent": "Mozilla/5.0"})

            # Try each API in sequence
            for api_index, api in enumerate(enabled_apis):
                api_name = api.get('name', f'API {api_index + 1}')
                api_url = api.get('url', '').strip() 
                success_code = api.get('success_code', 200)
                unavailable_code = api.get('unavailable_code', 404)
                
                if not api_url:
                    print(f"[DOWNLOAD] Skipping {api_name}: No URL configured")
                    continue
                
                download_url = api_url.replace('<appid>', str(app_id))
                print(f"[DOWNLOAD] Trying {api_name} ({api_index + 1}/{len(enabled_apis)}): {download_url}")
                
                # Configure timeout - faster connection timeout, longer read timeout
                timeout = httpx.Timeout(connect=3.0, read=float(api_timeout), write=5.0, pool=3.0)
                
                try:
                    print(f"[DOWNLOAD] Checking {api_name}...")
                    with client.stream("GET", download_url, timeout=timeout) as response:
                        status_code = response.status_code
                        print(f"[DOWNLOAD] {api_name} status: {status_code}")

                        if status_code == unavailable_code:
                            print(f"[DOWNLOAD] {api_name}: Not available")
                            continue  # Try next API immediately
                        elif status_code != success_code:
                            print(f"[DOWNLOAD] {api_name}: Error {status_code}")
                            continue  # Try next API immediately

                        # SUCCESS! Start download immediately
                        print(f"[DOWNLOAD] {api_name}: SUCCESS! Starting download...")
                        
                        # Quick file extension detection (no complex processing)
                        content_type = response.headers.get('content-type', '').lower()
                        if 'zip' in content_type:
                            file_extension = '.zip'
                        elif 'rar' in content_type:
                            file_extension = '.rar'
                        elif '7z' in content_type:
                            file_extension = '.7z'
                        else:
                            file_extension = '.lua'  # Default
                        
                        # Generate filename quickly
                        safe_game_name = game_name.replace(' ', '_').replace('/', '_').replace('\\', '_')
                        filename = f"{app_id}_{safe_game_name}{file_extension}"
                        file_path = os.path.join(download_dir, filename)
                    
                        # NOW start timing the actual download
                        download_start_time = time.time()
                        total_bytes = 0

                        print(f"[DOWNLOAD] Streaming {filename}...")
                        with open(file_path, "wb") as out_file:
                            for chunk in response.iter_bytes(chunk_size=8192):  # Larger chunks for speed
                                if chunk:
                                    out_file.write(chunk)
                                    total_bytes += len(chunk)

                        # Calculate download statistics (only actual download time)
                        download_end_time = time.time()
                        download_time = download_end_time - download_start_time
                        file_size_mb = total_bytes / (1024 * 1024)  # Convert to MB
                        speed_mbps = file_size_mb / download_time if download_time > 0 else 0
                        
                        print(f"[DOWNLOAD] SUCCESS: {app_id} via {api_name} - {file_size_mb:.2f} MB in {download_time:.1f}s ({speed_mbps:.2f} MB/s)")
                        
                        # Check if downloaded file is supported
                        if not self.is_downloaded_file_supported(file_path):
                            print(f"[DOWNLOAD] ERROR: Unsupported file type for {app_id}")
                            # Clean up the unsupported file
                            try:
                                os.remove(file_path)
                            except:
                                pass
                            continue  # Try next API
                        
                        # Process the downloaded file
                        print(f"[DOWNLOAD] Processing downloaded file for {app_id}...")
                        processing_success, processing_message = self.process_downloaded_file(file_path, app_id, game_name)
                        
                        if not processing_success:
                            print(f"[DOWNLOAD] ERROR: Failed to process file for {app_id}: {processing_message}")
                            # Clean up the file if processing failed
                            try:
                                os.remove(file_path)
                            except:
                                pass
                            continue  # Try next API
                        
                        # If backup is disabled, clean up the original file
                        if not self.settings.get('backup_downloads', False):
                            try:
                                os.remove(file_path)
                                print(f"[DOWNLOAD] Cleaned up original file (backup disabled)")
                            except Exception as e:
                                print(f"[DOWNLOAD] Warning: Could not clean up original file: {e}")
                        
                        # Return success with statistics
                        stats = {
                            'file_size_mb': file_size_mb,
                            'speed_mbps': speed_mbps,
                            'time_taken': download_time,
                            'filename': filename,
                            'api_used': api_name
                        }
                        
                        return True, f"Successfully downloaded via {api_name}: {processing_message}", stats
                    
                except httpx.TimeoutException:
                    print(f"[DOWNLOAD] {api_name}: TIMEOUT")
                    continue  # Try next API immediately
                except httpx.RequestError as e:
                    print(f"[DOWNLOAD] {api_name}: NETWORK ERROR - {str(e)}")
                    continue  # Try next API immediately
                except Exception as e:
                    print(f"[DOWNLOAD] {api_name}: ERROR - {str(e)}")
                    continue  # Try next API immediately
            
            # If we get here, all APIs failed
            print(f"[DOWNLOAD] FAILED: All {len(enabled_apis)} APIs failed for {app_id}")
            return False, f"All APIs failed for {app_id}"
            
        except Exception as e:
            print(f"[DOWNLOAD] CRITICAL ERROR: {app_id} - {str(e)}")
            return False, f"Download error: {str(e)}"

    def handle_download_result(self, success, message, button):
        """Handle the result of a download operation"""
        if success:
            # Download successful
            button.config(state='normal', text='Downloaded', bg='#4CAF50')  # Green
            # Show success message
            messagebox.showinfo("Download Success", message)
        else:
            # Download failed
            button.config(state='normal', text='Download', bg='#5c7e10')  # Reset to original
            # Show error message
            messagebox.showerror("Download Failed", message)

    def open_download_manager(self):
        """Open the download manager interface"""
        # Check if god_mode_frame exists
        if not hasattr(self, 'god_mode_frame'):
            messagebox.showerror("Error", "God Mode interface not available. Please open God Mode first.")
            return
            
        # Hide current God Mode content
        for widget in self.god_mode_frame.winfo_children():
            if hasattr(self, 'loading_frame') and widget != self.loading_frame:
                widget.pack_forget()
            elif not hasattr(self, 'loading_frame'):
                widget.pack_forget()
        
        # Create download manager frame
        self.download_manager_frame = tk.Frame(self.god_mode_frame, bg=self.colors['bg'])
        self.download_manager_frame.pack(fill=tk.BOTH, expand=True)
        
        # Header with title and back button
        header_frame = tk.Frame(self.download_manager_frame, bg=self.colors['bg'])
        header_frame.pack(fill=tk.X, pady=(0, 20))
        
        # Title
        title_label = tk.Label(
            header_frame,
            text="Download Manager",
            font=('Segoe UI', 16, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        title_label.pack(side=tk.LEFT)
        
        # Button frame for multiple buttons
        button_frame = tk.Frame(header_frame, bg=self.colors['bg'])
        button_frame.pack(side=tk.RIGHT)
        
        # Clear finished downloads button
        clear_finished_button = tk.Button(
            button_frame,
            text="Clear Finished",
            font=('Segoe UI', 9),
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=10,
            pady=5,
            cursor='hand2',
            command=self.clear_finished_downloads
        )
        clear_finished_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Restart Steam button
        restart_steam_button = tk.Button(
            button_frame,
            text="🔄 Restart Steam",
            font=('Segoe UI', 9),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=10,
            pady=5,
            cursor='hand2',
            command=self.restart_steam
        )
        restart_steam_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Back button - store reference for state updates
        self.download_manager_back_button = tk.Button(
            button_frame,
            text="← Back to Games",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=15,
            pady=5,
            cursor='hand2',
            command=self.back_to_games_from_download_manager
        )
        self.download_manager_back_button.pack(side=tk.LEFT)
        
        # Queue section
        queue_frame = tk.Frame(self.download_manager_frame, bg=self.colors['secondary_bg'])
        queue_frame.pack(fill=tk.BOTH, expand=True, padx=20)
        
        # Store reference to queue title for live updates
        self.queue_title_label = tk.Label(
            queue_frame,
            text="Download Queue:",
            font=('Segoe UI', 12, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['secondary_bg']
        )
        self.queue_title_label.pack(anchor='w', padx=15, pady=(15, 10))
        
        # Create scrollable queue list
        queue_canvas = tk.Canvas(queue_frame, bg=self.colors['secondary_bg'], highlightthickness=0)
        queue_scrollbar = ttk.Scrollbar(queue_frame, orient="vertical", command=queue_canvas.yview)
        self.queue_scrollable_frame = tk.Frame(queue_canvas, bg=self.colors['secondary_bg'])
        
        self.queue_scrollable_frame.bind(
            "<Configure>",
            lambda e: queue_canvas.configure(scrollregion=queue_canvas.bbox("all"))
        )
        
        window_id = queue_canvas.create_window((0, 0), window=self.queue_scrollable_frame, anchor="nw")
        queue_canvas.configure(yscrollcommand=queue_scrollbar.set)
        
        queue_canvas.pack(side="left", fill="both", expand=True, padx=(15, 0))
        queue_scrollbar.pack(side="right", fill="y")
        
        # Bind canvas resize
        def on_canvas_configure(event):
            queue_canvas.itemconfig(window_id, width=event.width)
        
        queue_canvas.bind("<Configure>", on_canvas_configure)
        
        # Bind mouse wheel to the entire download manager frame for scrolling anywhere
        def _on_mousewheel(event):
            # Scroll the queue canvas when mouse wheel is used anywhere in the download manager
            queue_canvas.yview_scroll(int(-1*(event.delta/120)), "units")
        
        # Bind to the download manager frame to catch all mouse wheel events
        self.download_manager_frame.bind("<MouseWheel>", _on_mousewheel)
        
        # Update the queue display
        self.update_download_queue_display()
        
        # Update back button state based on current queue status
        self.update_god_mode_back_button()
        
        # Update queue title text immediately
        self.update_queue_title_text()
        
        # Check if we should start downloads when the button is pressed
        dont_start_until_button = self.settings.get('dont_start_downloads_until_button_pressed', False)
        
        if dont_start_until_button:
            # Start processing if no current download and setting is enabled
            if not self.current_download and self.download_queue:
                print(f"[QUEUE] Starting queue processing (triggered by download button)")
                # Initialize batch tracking for this queue
                self.current_batch_completed = []
                self.current_batch_failed = []
                self.process_download_queue()
        else:
            # Start processing if no current download (original behavior)
            if not self.current_download:
                print(f"[QUEUE] Starting queue processing")
                # Initialize batch tracking for this queue
                self.current_batch_completed = []
                self.current_batch_failed = []
                self.process_download_queue()

    def update_god_mode_back_button(self):
        """Update the God Mode back button state based on queue status"""
        if not hasattr(self, 'god_mode_back_button') or not self.god_mode_back_button:
            return
        
        try:
            # Check if button still exists
            self.god_mode_back_button.winfo_exists()
        except tk.TclError:
            self.god_mode_back_button = None
            return
        
        # Check if there are any active downloads (queued or in progress)
        has_active_downloads = len(self.download_queue) > 0 or self.current_download is not None
        
        if has_active_downloads:
            # Disable button and gray it out
            self.god_mode_back_button.config(
                state='disabled',
                bg='#666666',  # Gray color
                fg='#999999',  # Light gray text
                cursor='arrow'
            )
        else:
            # Enable button with normal colors
            self.god_mode_back_button.config(
                state='normal',
                bg=self.colors['button_bg'],
                fg=self.colors['text'],
                cursor='hand2'
            )

    def update_god_mode_buttons(self):
        """Update both the back button and refresh button states based on queue status"""
        # Update back button
        self.update_god_mode_back_button()
        
        # Update refresh button
        if not hasattr(self, 'god_mode_refresh_button') or not self.god_mode_refresh_button:
            return
        
        try:
            # Check if button still exists
            self.god_mode_refresh_button.winfo_exists()
        except tk.TclError:
            self.god_mode_refresh_button = None
            return
        
        # Check if there are any active downloads (queued or in progress)
        has_active_downloads = len(self.download_queue) > 0 or self.current_download is not None
        
        if has_active_downloads:
            # Disable button and gray it out
            self.god_mode_refresh_button.config(
                state='disabled',
                bg='#666666',  # Gray color
                fg='#999999',  # Light gray text
                cursor='arrow'
            )
        else:
            # Enable button with normal colors
            self.god_mode_refresh_button.config(
                state='normal',
                bg=self.colors['button_bg'],
                fg=self.colors['text'],
                cursor='hand2'
            )

    def back_to_games_from_download_manager(self):
        """Return to the games list from download manager"""
        # Clear the back button reference
        self.download_manager_back_button = None
        
        if self.download_manager_frame:
            self.download_manager_frame.pack_forget()
        
        # Show the games list again
        if hasattr(self, 'god_mode_game_list') and hasattr(self, 'god_mode_steam_data'):
            self.show_god_mode_games(self.god_mode_game_list, self.god_mode_steam_data)

    def add_to_download_queue(self, app_id, game_name):
        """Add a download to the queue"""
        print(f"[QUEUE] Adding {game_name} (App ID: {app_id}) to download queue")
        download_item = {
            'app_id': app_id,
            'game_name': game_name,
            'status': 'queued',
            'progress': 0
        }
        self.download_queue.append(download_item)
        
        # Track this game as queued for persistent state
        self.queued_games.add(str(app_id))
        
        print(f"[QUEUE] Queue size: {len(self.download_queue)} items")
        self.update_download_queue_display()
        self.update_queue_title_text()
        
        # Update button states
        self.update_god_mode_buttons()
        
        # Check if we should start downloads automatically
        dont_start_until_button = self.settings.get('dont_start_downloads_until_button_pressed', False)
        
        if not dont_start_until_button:
            # Start processing downloads (multi-threaded behavior)
            max_threads = int(self.settings.get('max_download_threads', 3))
            current_active = len(self.active_downloads) + (1 if self.current_download else 0)
            
            if current_active == 0:
                print(f"[QUEUE] Starting queue processing")
                # Initialize batch tracking for this queue
                self.current_batch_completed = []
                self.current_batch_failed = []
            
            print(f"[QUEUE] {game_name} queued - {current_active}/{max_threads} downloads active")
            self.process_download_queue()
        else:
            print(f"[QUEUE] Downloads paused until download button is pressed, {game_name} queued")

    def process_download_queue(self):
        """Process the download queue"""
        if not self.download_queue:
            return
            
        max_threads = int(self.settings.get('max_download_threads', 3))
        current_active = len(self.active_downloads)
        
        print(f"[QUEUE] Processing queue: {len(self.download_queue)} queued, {current_active}/{max_threads} active downloads")
        
        # Legacy single-threaded fallback if max_threads is 1
        if max_threads == 1:
            # Use original single-threaded logic
            if self.current_download:
                return
        
        # Multi-threaded processing
        # Start new downloads up to the thread limit
        if max_threads > 1:
            while self.download_queue and current_active < max_threads:
                # Get next item from queue
                download_item = self.download_queue.pop(0)
                app_id = download_item['app_id']
                
                # Mark as downloading and add to active downloads
                download_item['status'] = 'downloading'
                self.active_downloads[str(app_id)] = download_item
                
                print(f"[QUEUE] Starting download: {download_item['game_name']} (App ID: {app_id})")
                
                # Start download in separate thread
                def download_thread(item=download_item):
                    app_id_str = str(item['app_id'])
                    print(f"[QUEUE] Download thread started for {item['game_name']}")
                    
                    try:
                        result = self.download_manifest(item['app_id'], item['game_name'])
                        
                        # Handle different return values
                        if len(result) == 3:  # Success case: (success, message, stats)
                            success, message, stats = result
                        else:  # Failure case: (success, message)
                            success, message = result
                            stats = None
                        
                        # Schedule the finish function to run on main thread
                        self.root.after(0, lambda: self.finish_single_download(app_id_str, success, message, stats))
                        
                    except Exception as e:
                        print(f"[QUEUE] Download thread error for {item['game_name']}: {e}")
                        # Schedule error handling on main thread
                        self.root.after(0, lambda: self.finish_single_download(app_id_str, False, f"Thread error: {str(e)}", None))
                
                # Create and start thread
                thread = threading.Thread(target=download_thread, daemon=True)
                self.download_threads[str(app_id)] = thread
                thread.start()
                
                current_active += 1
            
            # Update the display and return early for multi-threaded mode
            self.update_download_queue_display()
            self.update_queue_title_text()
            return
        
        # Continue with original single-threaded logic if max_threads is 1
        
        # Get next item from queue
        self.current_download = self.download_queue.pop(0)
        self.current_download['status'] = 'downloading'
        print(f"[QUEUE] Processing: {self.current_download['game_name']} (App ID: {self.current_download['app_id']})")
        print(f"[QUEUE] Remaining in queue: {len(self.download_queue)} items")
        
        # Update the display immediately
        self.update_download_queue_display()
        self.update_queue_title_text()
        
        # Start download in separate thread
        def download_thread():
            print(f"[QUEUE] Starting download thread for {self.current_download['game_name']}")
            result = self.download_manifest(self.current_download['app_id'], self.current_download['game_name'])
            
            # Handle different return values
            if len(result) == 3:  # Success case: (success, message, stats)
                success, message, stats = result
            else:  # Failure case: (success, message)
                success, message = result
                stats = None
            
            # Update UI on main thread
            self.root.after(0, lambda: self.finish_download(success, message, stats))
        
        thread = threading.Thread(target=download_thread, daemon=True)
        thread.start()

    def finish_download(self, success, message, stats=None):
        """Finish a download and process next in queue"""
        print(f"[QUEUE] Finishing download: {self.current_download['game_name']} - Success: {success}")
        
        # Update current download status
        if success:
            self.current_download['status'] = 'completed'
            # Store download statistics
            if stats:
                self.current_download['file_size_mb'] = stats['file_size_mb']
                self.current_download['speed_mbps'] = stats['speed_mbps']
                self.current_download['time_taken'] = stats['time_taken']
                self.current_download['filename'] = stats['filename']
                self.current_download['api_used'] = stats.get('api_used', 'Unknown API')  # Store API name
                print(f"[QUEUE] Download completed: {stats['filename']} ({stats['file_size_mb']:.2f} MB) via {self.current_download['api_used']}")
            
            # Remove from queued games set (now installed)
            app_id_str = str(self.current_download['app_id'])
            if app_id_str in self.queued_games:
                self.queued_games.remove(app_id_str)
                print(f"[QUEUE] Removed {app_id_str} from queued games (now installed)")
            
            # Move to completed downloads list
            self.completed_downloads.append(self.current_download)
            # Add to current batch
            if hasattr(self, 'current_batch_completed'):
                self.current_batch_completed.append(self.current_download)
            print(f"[QUEUE] Added to completed downloads list")
            
            # Immediately update the game card to show installed status
            self.update_game_card_after_download(
                self.current_download['app_id'], 
                self.current_download['game_name']
            )
        else:
            self.current_download['status'] = 'failed'
            self.current_download['error_message'] = message  # Store error message
            print(f"[QUEUE] Download failed: {message}")
            
            # Remove from queued games set (failed download)
            app_id_str = str(self.current_download['app_id'])
            if app_id_str in self.queued_games:
                self.queued_games.remove(app_id_str)
                print(f"[QUEUE] Removed {app_id_str} from queued games (download failed)")
            
            # Move to failed downloads list
            self.failed_downloads.append(self.current_download)
            # Add to current batch
            if hasattr(self, 'current_batch_failed'):
                self.current_batch_failed.append(self.current_download)
            print(f"[QUEUE] Added to failed downloads list")
        
        # Clear current download reference
        self.current_download = None
        
        # Update the display immediately
        self.update_download_queue_display()
        
        # Update button states
        self.update_god_mode_buttons()
        
        # Update queue title text immediately
        self.update_queue_title_text()
        
        # Process next item in queue
        if self.download_queue:
            print(f"[QUEUE] Processing next item in queue ({len(self.download_queue)} remaining)")
            self.process_download_queue()
        else:
            print(f"[QUEUE] Queue empty, no more downloads to process")
            
            # Show completion popup based on queue results
            self.show_completion_popup()

    def finish_single_download(self, app_id_str, success, message, stats=None):
        """Finish a single download in multi-threaded mode"""
        if app_id_str not in self.active_downloads:
            print(f"[QUEUE] Warning: finish_single_download called for unknown app_id {app_id_str}")
            return
            
        download_item = self.active_downloads[app_id_str]
        print(f"[QUEUE] Finishing download: {download_item['game_name']} - Success: {success}")
        
        # Update download status
        if success:
            download_item['status'] = 'completed'
            # Store download statistics
            if stats:
                download_item['file_size_mb'] = stats['file_size_mb']
                download_item['speed_mbps'] = stats['speed_mbps']
                download_item['time_taken'] = stats['time_taken']
                download_item['filename'] = stats['filename']
                download_item['api_used'] = stats.get('api_used', 'Unknown API')  # Store API name
                print(f"[QUEUE] Download completed: {stats['filename']} ({stats['file_size_mb']:.2f} MB) via {download_item['api_used']}")
            
            # Remove from queued games set (now installed)
            if app_id_str in self.queued_games:
                self.queued_games.remove(app_id_str)
                print(f"[QUEUE] Removed {app_id_str} from queued games (now installed)")
            
            # Move to completed downloads list
            self.completed_downloads.append(download_item)
            # Add to current batch
            if hasattr(self, 'current_batch_completed'):
                self.current_batch_completed.append(download_item)
            print(f"[QUEUE] Added to completed downloads list")
            
            # Immediately update the game card to show installed status
            self.update_game_card_after_download(
                download_item['app_id'], 
                download_item['game_name']
            )
        else:
            download_item['status'] = 'failed'
            download_item['error_message'] = message  # Store error message
            print(f"[QUEUE] Download failed: {message}")
            
            # Remove from queued games set (failed download)
            if app_id_str in self.queued_games:
                self.queued_games.remove(app_id_str)
                print(f"[QUEUE] Removed {app_id_str} from queued games (download failed)")
            
            # Move to failed downloads list
            self.failed_downloads.append(download_item)
            # Add to current batch
            if hasattr(self, 'current_batch_failed'):
                self.current_batch_failed.append(download_item)
            print(f"[QUEUE] Added to failed downloads list")
        
        # Remove from active downloads and threads
        del self.active_downloads[app_id_str]
        if app_id_str in self.download_threads:
            del self.download_threads[app_id_str]
        
        # Update the display immediately
        self.update_download_queue_display()
        
        # Update button states
        self.update_god_mode_buttons()
        
        # Update queue title text immediately
        self.update_queue_title_text()
        
        # Check if we should process more downloads from the queue
        self.process_download_queue()
        
        # Check if all downloads are complete
        if not self.download_queue and not self.active_downloads:
            print(f"[QUEUE] All downloads complete")
            
            # Show completion popup based on queue results
            self.show_completion_popup()

    def show_completion_popup(self):
        """Show completion popup based on download results"""
        # Use batch tracking if available, otherwise fall back to total counts
        if hasattr(self, 'current_batch_completed') and hasattr(self, 'current_batch_failed'):
            batch_completed = self.current_batch_completed
            batch_failed = self.current_batch_failed
        else:
            # Fallback: use all completed/failed downloads
            batch_completed = self.completed_downloads
            batch_failed = self.failed_downloads
        
        # Debug prints
        print(f"[POPUP] Debug - Completed downloads: {len(batch_completed)}")
        print(f"[POPUP] Debug - Failed downloads: {len(batch_failed)}")
        for item in batch_completed:
            print(f"[POPUP] Debug - Success: {item['game_name']} (App ID: {item['app_id']})")
        for item in batch_failed:
            print(f"[POPUP] Debug - Failed: {item['game_name']} (App ID: {item['app_id']})")
        
        total_processed = len(batch_completed) + len(batch_failed)
        
        if total_processed == 1:
            # Single item in queue - show immediate result
            if batch_completed:
                item = batch_completed[-1]  # Get the most recent completed item
                messagebox.showinfo(
                    "Download Complete", 
                    f"Successfully downloaded {item['game_name']} (App ID: {item['app_id']})"
                )
            elif batch_failed:
                item = batch_failed[-1]  # Get the most recent failed item
                messagebox.showerror(
                    "Download Failed", 
                    f"Failed to download {item['game_name']} (App ID: {item['app_id']}):\n{item['error_message']}"
                )
        else:
            # Multiple items in queue - show summary
            success_games = [f"{item['game_name']} ({item['app_id']})" for item in batch_completed]
            failed_games = [f"{item['game_name']} ({item['app_id']})" for item in batch_failed]
            
            # Build the message
            message_parts = []
            
            if success_games:
                success_text = f"Successfully downloaded:\n{chr(10).join(success_games)}"
                message_parts.append(success_text)
            
            if failed_games:
                failed_text = f"Failed to download:\n{chr(10).join(failed_games)}"
                message_parts.append(failed_text)
            
            if message_parts:
                # Determine the popup type and title
                if success_games and failed_games:
                    # Both successes and failures
                    popup_type = messagebox.showwarning
                    title = "Download Results"
                elif success_games:
                    # Only successes
                    popup_type = messagebox.showinfo
                    title = "Downloads Complete"
                else:
                    # Only failures
                    popup_type = messagebox.showerror
                    title = "Downloads Failed"
                
                message = "\n\n".join(message_parts)
                popup_type(title, message)
        
        # Clear batch tracking for next queue
        if hasattr(self, 'current_batch_completed'):
            self.current_batch_completed = []
        if hasattr(self, 'current_batch_failed'):
            self.current_batch_failed = []

    def update_download_queue_display(self):
        """Update the download queue display"""
        # Check if download manager is open and frame exists
        if not hasattr(self, 'queue_scrollable_frame') or not self.queue_scrollable_frame:
            return
        
        # Additional safety check to ensure the frame is still valid
        try:
            # Test if the frame is still valid by checking its window info
            self.queue_scrollable_frame.winfo_exists()
        except (tk.TclError, AttributeError):
            # Frame has been destroyed, clear the reference
            self.queue_scrollable_frame = None
            return
        
        # Clear existing items with error handling
        try:
            for widget in self.queue_scrollable_frame.winfo_children():
                widget.destroy()
        except (tk.TclError, AttributeError):
            # Frame became invalid during iteration, clear reference and return
            self.queue_scrollable_frame = None
            return
        
        # Add items to display with error handling
        try:
            # Add current download to display if exists (legacy single-threaded mode)
            if self.current_download:
                self.create_queue_item(self.current_download, self.queue_scrollable_frame)
            
            # Add active downloads (multi-threaded mode)
            for item in self.active_downloads.values():
                self.create_queue_item(item, self.queue_scrollable_frame)
            
            # Add queued items
            for item in self.download_queue:
                self.create_queue_item(item, self.queue_scrollable_frame)
            
            # Add completed downloads
            for item in self.completed_downloads:
                self.create_queue_item(item, self.queue_scrollable_frame)
            
            # Add failed downloads
            for item in self.failed_downloads:
                self.create_queue_item(item, self.queue_scrollable_frame)
        except (tk.TclError, AttributeError):
            # Frame became invalid during item creation, clear reference
            self.queue_scrollable_frame = None
            return
        
        # Update the queue title text
        self.update_queue_title_text()
    
    def update_queue_title_text(self):
        """Update the queue title text to show current status"""
        # Check if the queue title label exists and is valid
        if not hasattr(self, 'queue_title_label') or not self.queue_title_label:
            return
        
        try:
            # Test if the label is still valid and exists
            if not self.queue_title_label.winfo_exists():
                # Label has been destroyed, clear the reference
                self.queue_title_label = None
                return
        except (tk.TclError, AttributeError):
            # Label has been destroyed, clear the reference
            self.queue_title_label = None
            return
        
        # Calculate total items in various states
        queued_count = len(self.download_queue)
        active_count = len(self.active_downloads)
        current_download_count = 1 if self.current_download else 0
        
        # Total items that are not complete
        total_incomplete = queued_count + active_count + current_download_count
        
        try:
            if total_incomplete > 0:
                # There are items in queue or downloading
                self.queue_title_label.config(text=f"Download Queue: {total_incomplete}")
            else:
                # All downloads are complete
                self.queue_title_label.config(text="Download Queue Complete!")
        except (tk.TclError, AttributeError):
            # Label was destroyed during the config operation, clear the reference
            self.queue_title_label = None
            return
        
    def create_queue_item(self, item, parent_frame):
        """Create a queue item display"""
        try:
            item_frame = tk.Frame(parent_frame, bg=self.colors['bg'])
            item_frame.pack(fill=tk.X, padx=10, pady=5)
            
            # Status icon
            status_icons = {
                'queued': '⏳',
                'downloading': '⬇',
                'completed': '✅',
                'failed': '❌'
            }
            
            status_label = tk.Label(
                item_frame,
                text=status_icons.get(item['status'], '?'),
                font=('Segoe UI', 12),
                fg=self.colors['text'],
                bg=self.colors['bg']
            )
            status_label.pack(side=tk.LEFT, padx=(0, 10))
            
            # Game info
            info_frame = tk.Frame(item_frame, bg=self.colors['bg'])
            info_frame.pack(side=tk.LEFT, fill=tk.X, expand=True)
            
            game_name_label = tk.Label(
                info_frame,
                text=item['game_name'],
                font=('Segoe UI', 10, 'bold'),
                fg=self.colors['text'],
                bg=self.colors['bg'],
                anchor='w'
            )
            game_name_label.pack(anchor='w')
            
            app_id_label = tk.Label(
                info_frame,
                text=f"App ID: {item['app_id']}",
                font=('Segoe UI', 9),
                fg=self.colors['accent'],
                bg=self.colors['bg'],
                anchor='w'
            )
            app_id_label.pack(anchor='w')
            
            # Show download statistics for completed downloads
            if item['status'] == 'completed' and 'file_size_mb' in item:
                api_name = item.get('api_used', 'Unknown API')
                stats_text = f"Size: {item['file_size_mb']:.2f} MB | Speed: {item['speed_mbps']:.2f} MB/s | Time: {item['time_taken']:.1f}s | API: {api_name}"
                stats_label = tk.Label(
                    info_frame,
                    text=stats_text,
                    font=('Segoe UI', 8),
                    fg='#4CAF50',  # Green for completed
                    bg=self.colors['bg'],
                    anchor='w'
                )
                stats_label.pack(anchor='w')
            elif item['status'] == 'downloading' and 'api_used' in item:
                api_label = tk.Label(
                    info_frame,
                    text=f"Downloading via: {item['api_used']}",
                    font=('Segoe UI', 8),
                    fg=self.colors['accent'],
                    bg=self.colors['bg'],
                    anchor='w'
                )
                api_label.pack(anchor='w')
            
            # Show error message for failed downloads
            if item['status'] == 'failed' and 'error_message' in item:
                error_label = tk.Label(
                    info_frame,
                    text=f"Error: {item['error_message']}",
                    font=('Segoe UI', 8),
                    fg='#ff6b6b',  # Red color for errors
                    bg=self.colors['bg'],
                    anchor='w'
                )
                error_label.pack(anchor='w')
            
            # Status text and retry button for failed downloads
            status_frame = tk.Frame(item_frame, bg=self.colors['bg'])
            status_frame.pack(side=tk.RIGHT)
            
            status_text = {
                'queued': 'Queued',
                'downloading': 'Downloading...',
                'completed': 'Completed',
                'failed': 'Failed'
            }
            
            status_text_label = tk.Label(
                status_frame,
                text=status_text.get(item['status'], 'Unknown'),
                font=('Segoe UI', 9),
                fg=self.colors['accent'],
                bg=self.colors['bg']
            )
            status_text_label.pack(side=tk.TOP)
            
            # Add retry button for failed downloads
            if item['status'] == 'failed':
                retry_button = tk.Button(
                    status_frame,
                    text="Retry",
                    font=('Segoe UI', 8),
                    bg='#ff6b6b',
                    fg='white',
                    activebackground='#ff5252',
                    relief=tk.FLAT,
                    cursor='hand2',
                    command=lambda: self.retry_download(item)
                )
                retry_button.pack(side=tk.BOTTOM, pady=(2, 0))
                
        except (tk.TclError, AttributeError):
            # Parent frame is invalid or widget creation failed, skip creating this item
            return

    def retry_download(self, item):
        """Retry a failed download"""
        print(f"[RETRY] Retrying download for {item['game_name']} (App ID: {item['app_id']})")
        
        # Remove the failed item from failed downloads list
        if item in self.failed_downloads:
            self.failed_downloads.remove(item)
            print(f"[RETRY] Removed from failed downloads list")
        
        # Reset the item status
        item['status'] = 'queued'
        if 'error_message' in item:
            del item['error_message']
        
        # Add back to queue
        self.download_queue.append(item)
        print(f"[RETRY] Added back to queue (Queue size: {len(self.download_queue)})")
        self.update_download_queue_display()
        self.update_queue_title_text()
        
        # Update button states
        self.update_god_mode_buttons()
        
        # Start processing if no current download
        if not self.current_download:
            print(f"[RETRY] Starting queue processing for retry")
            self.process_download_queue()
        else:
            print(f"[RETRY] Download in progress, retry queued")

    def is_downloaded_file_supported(self, file_path):
        """Check if a downloaded file is supported (lua, zip, rar, 7z)"""
        if not os.path.exists(file_path):
            return False
        
        file_lower = file_path.lower()
        return (file_lower.endswith('.lua') or 
                file_lower.endswith(('.zip', '.rar', '.7z')))

    def process_downloaded_file(self, file_path, app_id, game_name):
        """Process a downloaded file - extract lua files and optionally save backup"""
        try:
            print(f"[DOWNLOAD] Processing downloaded file: {file_path}")
            
            # Check if file is supported
            if not self.is_downloaded_file_supported(file_path):
                print(f"[DOWNLOAD] ERROR: Unsupported file type for {app_id}")
                return False, f"Unsupported download: {os.path.basename(file_path)}"
            
            # Create temporary directory for processing
            temp_dir = tempfile.mkdtemp()
            lua_files = []
            
            try:
                # Process the file based on its type
                if file_path.lower().endswith('.lua'):
                    # Direct lua file
                    lua_files.append(file_path)
                else:
                    # Archive file - extract lua files
                    result = self.extract_lua_from_archive(file_path)
                    if isinstance(result, tuple) and len(result) == 2:
                        archive_lua_files, archive_temp_dir = result
                        lua_files.extend(archive_lua_files)
                        if archive_temp_dir:
                            temp_dir = archive_temp_dir  # Use the archive's temp dir
                    else:
                        return False, f"Failed to extract lua files from {os.path.basename(file_path)}"
                
                if not lua_files:
                    return False, f"No lua files found in {os.path.basename(file_path)}"
                
                # Process the lua files (same as drag & drop) - but don't show popup for downloads
                self.process_lua_files(lua_files, [temp_dir] if temp_dir else None, show_popup=False)
                
                # Save backup if setting is enabled
                if self.settings.get('backup_downloads', False):
                    backup_dir = os.path.join(os.path.dirname(self.settings_file), 'melly-downloads')
                    os.makedirs(backup_dir, exist_ok=True)
                    
                    safe_game_name = game_name.replace(' ', '_').replace('/', '_').replace('\\', '_')
                    backup_filename = f"{app_id}_{safe_game_name}{os.path.splitext(file_path)[1]}"
                    backup_path = os.path.join(backup_dir, backup_filename)
                    
                    shutil.copy2(file_path, backup_path)
                    print(f"[DOWNLOAD] Saved backup: {backup_path}")
                
                # Immediately update the game card to show installed status
                self.update_game_card_after_download(app_id, game_name)
                
                return True, f"Successfully processed {len(lua_files)} lua files"
                
            finally:
                # Clean up temporary directory
                if temp_dir and os.path.exists(temp_dir):
                    try:
                        shutil.rmtree(temp_dir)
                    except Exception as e:
                        print(f"[DOWNLOAD] Warning: Could not clean up temp dir {temp_dir}: {e}")
                        
        except Exception as e:
            print(f"[DOWNLOAD] ERROR: Failed to process downloaded file: {e}")
            return False, f"Processing error: {str(e)}"

    def update_game_card_after_download(self, app_id, game_name):
        """Update a game card immediately after successful download and processing"""
        try:
            print(f"[UPDATE] Updating game card for {game_name} (App ID: {app_id})")
            
            # First, update the game list data
            if hasattr(self, 'god_mode_game_list'):
                # Find the game in the list
                game_to_update = None
                for game in self.god_mode_game_list:
                    if str(game['app_id']) == str(app_id):
                        game_to_update = game
                        break
                
                if game_to_update:
                    # Update the game data
                    game_to_update['is_installed'] = True
                    game_to_update['lua_file'] = f"{app_id}.lua"
                    game_to_update['is_disabled'] = False
                    print(f"[UPDATE] Updated game data: {game_name} (App ID: {app_id})")
                    
                    # Update the search cache for this game
                    self.update_search_cache_for_game(app_id, is_installed=True, is_disabled=False, lua_file=f"{app_id}.lua")
                else:
                    # Add new game to the list
                    new_game = {
                        'app_id': app_id,
                        'game_name': game_name,
                        'is_installed': True,
                        'lua_file': f"{app_id}.lua",
                        'is_disabled': False
                    }
                    self.god_mode_game_list.append(new_game)
                    game_to_update = new_game
                    print(f"[UPDATE] Added new game to list: {game_name} (App ID: {app_id})")
                    
                    # Update the search cache for this new game
                    self.update_search_cache_for_game(app_id, is_installed=True, is_disabled=False, lua_file=f"{app_id}.lua")
                
                # Now find and update the actual game card in the UI
                self.update_game_card_in_ui(app_id, game_to_update)
                
        except Exception as e:
            print(f"[UPDATE] Error updating game card after download: {e}")

    def update_game_card_in_ui(self, app_id, game_data):
        """Find and update a specific game card in the UI"""
        try:
            # Find the scrollable frame that contains the game cards
            if hasattr(self, 'god_mode_frame') and self.god_mode_frame.winfo_exists():
                for widget in self.god_mode_frame.winfo_children():
                    if widget.winfo_name() == '!frame2':  # Games frame
                        games_frame = widget
                        
                        # Find the canvas and scrollable frame
                        for child in games_frame.winfo_children():
                            if isinstance(child, tk.Canvas):
                                canvas = child
                                # Get the scrollable frame from the canvas
                                scrollable_frame = None
                                for item in canvas.find_all():
                                    if canvas.type(item) == 'window':
                                        scrollable_frame = canvas.itemcget(item, 'window')
                                        break
                                
                                if scrollable_frame:
                                    # Find the specific game card
                                    for card in scrollable_frame.winfo_children():
                                        if isinstance(card, tk.Frame):
                                            # Check if this card contains the app_id we're looking for
                                            if self.card_contains_app_id(card, app_id):
                                                print(f"[UPDATE] Found game card for {app_id}, updating...")
                                                # Update the card in place
                                                self.update_game_card_in_place(game_data, card)
                                                return
                                    
                                    # If we didn't find the card, it might be a new game
                                    # We need to add it to the display
                                    print(f"[UPDATE] Game card not found for {app_id}, adding new card...")
                                    self.create_game_card(game_data, scrollable_frame)
                                    
                                    # Update canvas scroll region
                                    canvas.configure(scrollregion=canvas.bbox("all"))
                                    break
                        break
                        
        except Exception as e:
            print(f"[UPDATE] Error updating game card in UI: {e}")

    def card_contains_app_id(self, card, app_id):
        """Check if a game card contains the specified app_id"""
        try:
            # First check if the card has the app_id attribute
            if hasattr(card, 'app_id') and str(card.app_id) == str(app_id):
                return True
            
            # Recursively search through all widgets in the card
            def search_widget(widget):
                if isinstance(widget, tk.Label):
                    text = widget.cget('text')
                    if text and str(app_id) in text:
                        return True
                
                # Search children
                for child in widget.winfo_children():
                    if search_widget(child):
                        return True
                
                return False
            
            return search_widget(card)
            
        except Exception as e:
            print(f"[SEARCH] Error searching card for app_id {app_id}: {e}")
            return False

    def disable_game(self, app_id, game_name):
        """Disable a game by renaming its lua file to .disabled"""
        try:
            steam_path = self.get_steam_install_path()
            if not steam_path:
                return False, "Could not find Steam installation path"
            
            stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
            if not os.path.exists(stplugin_path):
                return False, "Could not find stplug-in directory"
            
            # Look for the lua file
            lua_file = os.path.join(stplugin_path, f"{app_id}.lua")
            if not os.path.exists(lua_file):
                return False, f"Lua file not found for {app_id}"
            
            # Rename to .disabled
            disabled_file = os.path.join(stplugin_path, f"{app_id}.lua.disabled")
            if os.path.exists(disabled_file):
                return False, f"Disabled file already exists for {app_id}"
            
            os.rename(lua_file, disabled_file)
            print(f"[DISABLE] Disabled game {app_id} ({game_name})")
            
            # Update the search cache to reflect the disable action
            self.update_search_cache_for_game(app_id, is_installed=True, is_disabled=True, lua_file=f"{app_id}.lua.disabled")
            
            return True, f"Successfully disabled {game_name}"
            
        except Exception as e:
            print(f"[DISABLE] ERROR: Failed to disable {app_id}: {e}")
            return False, f"Error disabling game: {str(e)}"

    def enable_game(self, app_id, game_name):
        """Enable a game by renaming its .disabled file back to .lua"""
        try:
            steam_path = self.get_steam_install_path()
            if not steam_path:
                return False, "Could not find Steam installation path"
            
            stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
            if not os.path.exists(stplugin_path):
                return False, "Could not find stplug-in directory"
            
            # Look for the disabled file
            disabled_file = os.path.join(stplugin_path, f"{app_id}.lua.disabled")
            if not os.path.exists(disabled_file):
                return False, f"Disabled file not found for {app_id}"
            
            # Rename back to .lua
            lua_file = os.path.join(stplugin_path, f"{app_id}.lua")
            if os.path.exists(lua_file):
                return False, f"Lua file already exists for {app_id}"
            
            os.rename(disabled_file, lua_file)
            print(f"[ENABLE] Enabled game {app_id} ({game_name})")
            
            # Update the search cache to reflect the enable action
            self.update_search_cache_for_game(app_id, is_installed=True, is_disabled=False, lua_file=f"{app_id}.lua")
            
            return True, f"Successfully enabled {game_name}"
            
        except Exception as e:
            print(f"[ENABLE] ERROR: Failed to enable {app_id}: {e}")
            return False, f"Error enabling game: {str(e)}"

    def delete_lua_file(self, app_id, game_name):
        """Delete a lua file completely"""
        try:
            steam_path = self.get_steam_install_path()
            if not steam_path:
                return False, "Could not find Steam installation path"
            
            stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
            if not os.path.exists(stplugin_path):
                return False, "Could not find stplug-in directory"
            
            # Look for the lua file (both normal and disabled)
            lua_file = os.path.join(stplugin_path, f"{app_id}.lua")
            disabled_file = os.path.join(stplugin_path, f"{app_id}.lua.disabled")
            
            file_to_delete = None
            if os.path.exists(lua_file):
                file_to_delete = lua_file
            elif os.path.exists(disabled_file):
                file_to_delete = disabled_file
            else:
                return False, f"Lua file not found for {app_id}"
            
            # Delete the file
            os.remove(file_to_delete)
            print(f"[DELETE] Deleted lua file for game {app_id} ({game_name})")
            
            # Update the search cache to reflect the deletion
            self.update_search_cache_for_game(app_id, is_installed=False, is_disabled=False, lua_file=None)
            
            return True, f"Successfully deleted {game_name}"
            
        except Exception as e:
            print(f"[DELETE] ERROR: Failed to delete {app_id}: {e}")
            return False, f"Error deleting game: {str(e)}"

    def is_game_disabled(self, app_id):
        """Check if a game is disabled by looking for .disabled file"""
        try:
            steam_path = self.get_steam_install_path()
            if not steam_path:
                return False
            
            stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
            if not os.path.exists(stplugin_path):
                return False
            
            disabled_file = os.path.join(stplugin_path, f"{app_id}.lua.disabled")
            return os.path.exists(disabled_file)
            
        except Exception as e:
            print(f"[DISABLE] ERROR: Could not check disabled status for {app_id}: {e}")
            return False

    def update_search_cache_for_game(self, app_id, is_installed, is_disabled, lua_file):
        """Update the search cache for a specific game after disable/enable/delete operations"""
        app_id_str = str(app_id)
        
        # Update the cache if it exists (only when god mode is active)
        if hasattr(self, 'steam_search_cache') and hasattr(self, 'installed_games_dict'):
            # Update steam_search_cache
            for game_data in self.steam_search_cache:
                if game_data['app_id'] == app_id_str:
                    game_data['is_installed'] = is_installed
                    game_data['is_disabled'] = is_disabled
                    game_data['lua_file'] = lua_file
                    break
            
            # Update installed_games_dict
            if is_installed:
                # Game is still installed (disabled or enabled)
                if app_id_str in self.installed_games_dict:
                    self.installed_games_dict[app_id_str]['is_disabled'] = is_disabled
                    self.installed_games_dict[app_id_str]['lua_file'] = lua_file
                else:
                    # Game wasn't in dict but now is installed, shouldn't happen but handle it
                    self.installed_games_dict[app_id_str] = {
                        'app_id': app_id_str,
                        'game_name': 'Unknown Game',  # We don't have the name here
                        'is_disabled': is_disabled,
                        'lua_file': lua_file
                    }
            else:
                # Game was deleted, remove from installed_games_dict
                if app_id_str in self.installed_games_dict:
                    del self.installed_games_dict[app_id_str]
            
            print(f"[CACHE] Updated cache for game {app_id_str}: installed={is_installed}, disabled={is_disabled}, file={lua_file}")

    def refresh_game_list(self):
        """Update the game list to show updated status after downloads or changes (no full API refresh)"""
        try:
            # Check if we're currently in the God Mode games view
            if hasattr(self, 'god_mode_frame') and self.god_mode_frame.winfo_exists():
                # Check if we're showing games (not loading or error)
                for widget in self.god_mode_frame.winfo_children():
                    if widget.winfo_name() == '!frame2':  # This is the games frame
                        # Only do a local refresh - no full API call
                        self.update_game_list_locally()
                        return
        except Exception as e:
            print(f"[REFRESH] Error refreshing game list: {e}")

    def update_game_list_locally(self):
        """Update the game list locally by rechecking .lua files without full API refresh"""
        try:
            print("[UPDATE] Updating game list locally...")
            
            # Get the current Steam plugin path
            steam_path = self.get_steam_install_path()
            if not steam_path:
                print("[UPDATE] Could not find Steam installation path")
                return
            
            stplugin_path = os.path.join(steam_path, "config", "stplug-in")
            if not os.path.exists(stplugin_path):
                print("[UPDATE] Steam plugin directory not found")
                return
            
            # Recheck .lua files locally
            lua_files, disabled_files = self.find_lua_files(stplugin_path)
            
            # Update the existing game list with new information
            if hasattr(self, 'god_mode_game_list'):
                updated_games = []
                
                for game in self.god_mode_game_list:
                    app_id = game['app_id']
                    
                    # Check if this game is now installed
                    lua_file = None
                    is_disabled = False
                    
                    # Check for regular .lua file
                    for lua_file_path in lua_files:
                        if os.path.basename(lua_file_path).startswith(f"{app_id}."):
                            lua_file = os.path.basename(lua_file_path)
                            break
                    
                    # Check for disabled .lua file
                    if not lua_file:
                        for disabled_file_path in disabled_files:
                            if os.path.basename(disabled_file_path).startswith(f"{app_id}."):
                                lua_file = os.path.basename(disabled_file_path)
                                is_disabled = True
                                break
                    
                    # Update game status
                    game['is_installed'] = lua_file is not None
                    game['lua_file'] = lua_file
                    game['is_disabled'] = is_disabled
                    
                    updated_games.append(game)
                
                # Update the stored game list
                self.god_mode_game_list = updated_games
                
                # Refresh the display without reloading from API
                self.refresh_game_display_only()
                
                print(f"[UPDATE] Game list updated locally - {len(updated_games)} games")
            
        except Exception as e:
            print(f"[UPDATE] Error updating game list locally: {e}")

    def refresh_game_display_only(self):
        """Refresh only the game display without reloading data"""
        try:
            # Find the scrollable frame that contains the game cards
            if hasattr(self, 'god_mode_frame') and self.god_mode_frame.winfo_exists():
                for widget in self.god_mode_frame.winfo_children():
                    if widget.winfo_name() == '!frame2':  # Games frame
                        games_frame = widget
                        
                        # Find the canvas and scrollable frame
                        for child in games_frame.winfo_children():
                            if isinstance(child, tk.Canvas):
                                canvas = child
                                # Get the scrollable frame from the canvas
                                scrollable_frame = None
                                for item in canvas.find_all():
                                    if canvas.type(item) == 'window':
                                        scrollable_frame = canvas.itemcget(item, 'window')
                                        break
                                
                                if scrollable_frame:
                                    # Clear existing game cards
                                    for card in scrollable_frame.winfo_children():
                                        card.destroy()
                                    
                                    # Recreate game cards with updated data
                                    if hasattr(self, 'god_mode_game_list'):
                                        for game in self.god_mode_game_list:
                                            self.create_game_card(game, scrollable_frame)
                                    
                                    # Update canvas scroll region
                                    canvas.configure(scrollregion=canvas.bbox("all"))
                                    break
                        break
                        
        except Exception as e:
            print(f"[DISPLAY] Error refreshing game display: {e}")

    def add_new_game_to_list(self, app_id, game_name, lua_file=None, is_disabled=False):
        """Add a new game to the existing game list (for downloads)"""
        try:
            if hasattr(self, 'god_mode_game_list'):
                # Check if game already exists in the list
                existing_game = None
                for game in self.god_mode_game_list:
                    if game['app_id'] == app_id:
                        existing_game = game
                        break
                
                if existing_game:
                    # Update existing game
                    existing_game['is_installed'] = True
                    existing_game['lua_file'] = lua_file or f"{app_id}.lua"
                    existing_game['is_disabled'] = is_disabled
                    print(f"[UPDATE] Updated existing game: {game_name} (App ID: {app_id})")
                else:
                    # Add new game to the list
                    new_game = {
                        'app_id': app_id,
                        'game_name': game_name,
                        'is_installed': True,
                        'lua_file': lua_file or f"{app_id}.lua",
                        'is_disabled': is_disabled
                    }
                    self.god_mode_game_list.append(new_game)
                    print(f"[UPDATE] Added new game: {game_name} (App ID: {app_id})")
                
                # Refresh the display
                self.refresh_game_display_only()
                
        except Exception as e:
            print(f"[UPDATE] Error adding new game to list: {e}")

    def update_game_card_in_place(self, game, outer_frame):
        """Update a specific game card in place without refreshing the entire list"""
        try:
            # Get the current game status
            is_installed = game.get('is_installed', True)
            is_disabled = game.get('is_disabled', False)
            
            # Determine new background color
            if is_disabled:
                bg_color = '#4a1a1a'  # Dark red for disabled games
            elif is_installed:
                bg_color = self.colors['secondary_bg']  # Normal color for installed games
            else:
                bg_color = '#1e3a4a'  # Darker blue for non-installed
            
            # Function to recursively update all child widgets
            def update_widget_bg(widget, new_bg):
                """Recursively update background color of widget and all its children"""
                try:
                    widget.configure(bg=new_bg)
                except:
                    pass  # Some widgets might not support bg configuration
                
                # Update all children
                for child in widget.winfo_children():
                    update_widget_bg(child, new_bg)
            
            # Ensure the top frame maintains its fixed dimensions
            def ensure_frame_dimensions(frame):
                """Ensure frame maintains consistent dimensions"""
                try:
                    # Force the frame to maintain its height
                    frame.configure(height=50)
                    frame.pack_propagate(False)
                except:
                    pass
            
            # Update the entire card hierarchy
            update_widget_bg(outer_frame, bg_color)
            
            # Now update specific elements that need text/color changes
            for widget in outer_frame.winfo_children():
                if isinstance(widget, tk.Frame):
                    inner_frame = widget
                    
                    # Update top frame
                    for top_widget in inner_frame.winfo_children():
                        if isinstance(top_widget, tk.Frame):
                            top_frame = top_widget
                            # Ensure consistent dimensions
                            ensure_frame_dimensions(top_frame)
                            
                            # Update game name label
                            for name_widget in top_frame.winfo_children():
                                if isinstance(name_widget, tk.Label) and name_widget.cget('text') and ('✅' in name_widget.cget('text') or '🔴' in name_widget.cget('text') or '❌' in name_widget.cget('text') or '🟢' in name_widget.cget('text')):
                                    # Update game name and status
                                    if is_disabled:
                                        status_icon = "🔴"  # Red circle for disabled
                                        game_name_text = f"{status_icon} {game['game_name']} (DISABLED)"
                                        text_color = '#ff6b6b'  # Red text for disabled
                                    elif is_installed:
                                        status_icon = "🟢"  # Green circle for enabled
                                        game_name_text = f"{status_icon} {game['game_name']}"
                                        text_color = self.colors['text']
                                    else:
                                        status_icon = "❌"  # Red X for not installed
                                        game_name_text = f"{status_icon} {game['game_name']}"
                                        text_color = '#888888'  # Grayed out for non-installed
                                    
                                    # Update the label while maintaining consistent dimensions
                                    name_widget.configure(
                                        text=game_name_text, 
                                        fg=text_color, 
                                        bg=bg_color,
                                        wraplength=0,  # No text wrapping
                                        justify=tk.LEFT
                                    )
                                    break
                            
                            # Remove all existing buttons first - search more thoroughly
                            buttons_to_remove = []
                            
                            # Search recursively through all widgets in the top frame
                            def find_all_buttons(widget):
                                if isinstance(widget, tk.Button):
                                    buttons_to_remove.append(widget)
                                # Search children recursively
                                for child in widget.winfo_children():
                                    find_all_buttons(child)
                            
                            # Find all buttons in the top frame and its children
                            find_all_buttons(top_frame)
                            
                            print(f"[UPDATE] Found {len(buttons_to_remove)} buttons to remove for {game['game_name']}")
                            
                            # Destroy all found buttons
                            for button in buttons_to_remove:
                                try:
                                    button.destroy()
                                    print(f"[UPDATE] Destroyed button: {button.cget('text') if hasattr(button, 'cget') else 'unknown'}")
                                except:
                                    pass  # Button might already be destroyed
                            
                            # Use after() to delay button creation to prevent duplication
                            # Also ensure consistent button layout to prevent frame warping
                            def create_buttons():
                                # Ensure frame dimensions are stable before creating buttons
                                ensure_frame_dimensions(top_frame)
                                
                                # Double-check that no buttons exist before creating new ones
                                def check_and_create():
                                    # Final check for any remaining buttons
                                    remaining_buttons = []
                                    def find_remaining_buttons(widget):
                                        if isinstance(widget, tk.Button):
                                            remaining_buttons.append(widget)
                                        for child in widget.winfo_children():
                                            find_remaining_buttons(child)
                                    
                                    find_remaining_buttons(top_frame)
                                    
                                    # If no buttons remain, create new ones
                                    if not remaining_buttons:
                                        self._create_buttons_impl(top_frame, game, is_installed, is_disabled)
                                    else:
                                        # Still have buttons, destroy them and try again
                                        for button in remaining_buttons:
                                            try:
                                                button.destroy()
                                            except:
                                                pass
                                        # Create new buttons after a short delay
                                        self.root.after(5, lambda: self._create_buttons_impl(top_frame, game, is_installed, is_disabled))
                                
                                # Small delay to prevent visual warping
                                self.root.after(10, check_and_create)
                            
                            # Call the button creation function with delay to prevent warping
                            create_buttons()
                            break
                    
                    # Update info frame and its children
                    for info_widget in inner_frame.winfo_children():
                        if isinstance(info_widget, tk.Frame):
                            info_frame = info_widget
                            
                            # Update details frame
                            for details_widget in info_frame.winfo_children():
                                if isinstance(details_widget, tk.Frame):
                                    details_frame = details_widget
                                    
                                    # Update all labels in details frame
                                    for detail_widget in details_frame.winfo_children():
                                        if isinstance(detail_widget, tk.Label):
                                            # Update App ID label
                                            if 'App ID:' in detail_widget.cget('text', ''):
                                                if is_disabled:
                                                    detail_widget.configure(fg='#666666', bg=bg_color)
                                                else:
                                                    detail_widget.configure(fg=self.colors['accent'], bg=bg_color)
                                            # Update separator label ( | )
                                            elif '|' in detail_widget.cget('text', ''):
                                                if is_installed and game.get('lua_file'):
                                                    detail_widget.configure(fg=self.colors['accent'] if not is_disabled else '#666666', bg=bg_color)
                                                else:
                                                    # Hide separator if no file to show
                                                    detail_widget.pack_forget()
                                            # Update file label
                                            elif 'File:' in detail_widget.cget('text', ''):
                                                if is_installed and game.get('lua_file'):
                                                    if is_disabled:
                                                        file_text = f"File: {game['lua_file']} (DISABLED)"
                                                        file_color = '#ff6b6b'  # Red for disabled
                                                    else:
                                                        file_text = f"File: {game['lua_file']}"
                                                        file_color = self.colors['accent']
                                                    
                                                    print(f"[UPDATE] Updating file label to: {file_text}")
                                                    detail_widget.configure(text=file_text, fg=file_color, bg=bg_color)
                                                    # Show the file label
                                                    detail_widget.pack(side=tk.LEFT)
                                                else:
                                                    # Hide file label if not installed
                                                    print(f"[UPDATE] Hiding file label (not installed)")
                                                    detail_widget.pack_forget()
                                            else:
                                                # Update any other labels
                                                detail_widget.configure(bg=bg_color)
                                    
                                    # Check if we need to create or update file labels (if show_file_names is enabled)
                                    show_file_names = self.settings.get('show_file_names', False)
                                    print(f"[UPDATE] File labels - show_file_names: {show_file_names}, is_installed: {is_installed}, lua_file: {game.get('lua_file')}")
                                    if show_file_names and is_installed and game.get('lua_file'):
                                        # Check if file labels already exist and update them
                                        has_separator = False
                                        has_file_label = False
                                        existing_file_label = None
                                        existing_separator = None
                                        
                                        for detail_widget in details_frame.winfo_children():
                                            if isinstance(detail_widget, tk.Label):
                                                if '|' in detail_widget.cget('text', ''):
                                                    has_separator = True
                                                    existing_separator = detail_widget
                                                elif 'File:' in detail_widget.cget('text', ''):
                                                    has_file_label = True
                                                    existing_file_label = detail_widget
                                        
                                        # Update or create separator
                                        if has_separator and existing_separator:
                                            # Update existing separator
                                            existing_separator.configure(
                                                text="  |  ",
                                                fg=self.colors['accent'] if not is_disabled else '#666666',
                                                bg=bg_color
                                            )
                                        elif not has_separator:
                                            print(f"[UPDATE] Creating separator label")
                                            separator_label = tk.Label(
                                                details_frame,
                                                text="  |  ",
                                                font=('Segoe UI', 10),
                                                fg=self.colors['accent'] if not is_disabled else '#666666',
                                                bg=bg_color
                                            )
                                            separator_label.pack(side=tk.LEFT)
                                        
                                        # Update or create file label
                                        if has_file_label and existing_file_label:
                                            # Update existing file label with new status
                                            if is_disabled:
                                                file_text = f"File: {game['lua_file']} (DISABLED)"
                                                file_color = '#ff6b6b'  # Red for disabled
                                            else:
                                                file_text = f"File: {game['lua_file']}"
                                                file_color = self.colors['accent']
                                            
                                            print(f"[UPDATE] Updating existing file label to: {file_text}")
                                            existing_file_label.configure(
                                                text=file_text,
                                                fg=file_color,
                                                bg=bg_color
                                            )
                                        elif not has_file_label:
                                            print(f"[UPDATE] Creating file label")
                                            if is_disabled:
                                                file_text = f"File: {game['lua_file']} (DISABLED)"
                                                file_color = '#ff6b6b'  # Red for disabled
                                            else:
                                                file_text = f"File: {game['lua_file']}"
                                                file_color = self.colors['accent']
                                            
                                            file_name_label = tk.Label(
                                                details_frame,
                                                text=file_text,
                                                font=('Segoe UI', 10),
                                                fg=file_color,
                                                bg=bg_color,
                                                anchor='w',
                                                cursor='ibeam'
                                            )
                                            file_name_label.pack(side=tk.LEFT)
                                            
                                            # Add double-click to copy file name
                                            def copy_file_name(event):
                                                self.root.clipboard_clear()
                                                self.root.clipboard_append(game['lua_file'])
                                                # Visual feedback
                                                original_bg = file_name_label.cget('bg')
                                                file_name_label.configure(bg='#4a4a4a')
                                                self.root.after(200, lambda: file_name_label.configure(bg=original_bg))
                                                return 'break'
                                            
                                            file_name_label.bind('<Double-Button-1>', copy_file_name)
                                    else:
                                        # Remove file labels if they shouldn't be shown
                                        print(f"[UPDATE] Removing file labels (not needed)")
                                        labels_to_remove = []
                                        for detail_widget in details_frame.winfo_children():
                                            if isinstance(detail_widget, tk.Label):
                                                if '|' in detail_widget.cget('text', '') or 'File:' in detail_widget.cget('text', ''):
                                                    labels_to_remove.append(detail_widget)
                                        
                                        print(f"[UPDATE] Found {len(labels_to_remove)} file labels to remove")
                                        for label in labels_to_remove:
                                            try:
                                                print(f"[UPDATE] Removing label: {label.cget('text')}")
                                                label.destroy()
                                            except:
                                                pass
                                    
                                    break
                            break
                    break
            
            # Force update the display
            outer_frame.update_idletasks()
            
        except Exception as e:
            print(f"[UPDATE] Error updating game card in place: {e}")
            # Fallback to full refresh if in-place update fails
            self.refresh_god_mode_data()

    def clear_download_queue(self):
        """Clear the download queue and reset current download"""
        self.download_queue.clear()
        self.current_download = None
        self.completed_downloads.clear()
        self.failed_downloads.clear()
        self.queued_games.clear()  # Clear persistent queued state
        
        # Clear multi-threaded downloads (note: threads will complete naturally)
        self.active_downloads.clear()
        self.download_threads.clear()
        
        print("[QUEUE] Download queue cleared")
        
        # Update button states
        self.update_god_mode_buttons()
        
        # Update queue title text
        self.update_queue_title_text()
        
    def clear_finished_downloads(self):
        """Clear only completed and failed downloads from the queue"""
        self.completed_downloads.clear()
        self.failed_downloads.clear()
        
        # Update the display
        self.update_download_queue_display()
        
        print("[QUEUE] Cleared finished downloads")
        
        # Update download manager display if it's open
        if hasattr(self, 'queue_scrollable_frame') and self.queue_scrollable_frame.winfo_exists():
            self.update_download_queue_display()
            self.update_queue_title_text()

    def create_game_card(self, game, parent_frame):
        """Create a game card widget for the God Mode games list"""
        # Determine if game is installed and disabled
        is_installed = game.get('is_installed', True)  # Default to True for backward compatibility
        is_disabled = game.get('is_disabled', False)
        
        # Create outer frame for border effect with modern styling
        if is_disabled:
            bg_color = '#4a1a1a'  # Dark red for disabled games
        elif is_installed:
            bg_color = self.colors['card_bg']  # Modern card color for installed games
        else:
            bg_color = '#1e3a4a'  # Darker blue for non-installed
        
        outer_frame = self.create_modern_frame(
            parent_frame,
            bg=bg_color
        )
        outer_frame.pack(fill=tk.X, padx=(0, 10), pady=8)
        
        # Store the app_id as an attribute for easy identification
        outer_frame.app_id = game['app_id']
        
        # Create inner frame for content (same size as outer frame)
        game_frame = tk.Frame(
            outer_frame,
            bg=bg_color,
            relief=tk.FLAT,
            bd=0
        )
        game_frame.pack(fill=tk.BOTH, expand=True)
        
        # Top frame for game name and action button - fixed dimensions to prevent warping
        top_frame = tk.Frame(game_frame, bg=bg_color, height=50)  # Fixed height
        top_frame.pack(fill=tk.X, padx=20, pady=(15, 8))
        top_frame.pack_propagate(False)  # Prevent frame from resizing based on content
        
        # Add appropriate button based on game status
        action_button = None
        if is_installed:
                            # Nuke button (🗑) - always show for installed games
            def nuke_game():
                # Show confirmation dialog
                result = messagebox.askyesno(
                    "Confirm Deletion",
                    f"Are you sure you want to DELETE the lua file for '{game['game_name']}'?\n\n"
                    "This will permanently remove the file and cannot be undone!",
                    icon='warning'
                )
                if result:
                    success, message = self.delete_lua_file(game['app_id'], game['game_name'])
                    if success:
                        # Update the game status immediately
                        game['is_installed'] = False
                        game['lua_file'] = None
                        # Update the card in place
                        self.update_game_card_in_place(game, outer_frame)
                    else:
                        messagebox.showerror("Error", message)
            
            if is_disabled:
                # Enable button (green tick mark) - for disabled games
                def enable_game():
                    success, message = self.enable_game(game['app_id'], game['game_name'])
                    if success:
                        # Update the game status immediately
                        game['is_disabled'] = False
                        game['lua_file'] = game['lua_file'].replace('.disabled', '')
                        # Update the card in place
                        self.update_game_card_in_place(game, outer_frame)
                    else:
                        messagebox.showerror("Error", message)
                
                action_button = self.create_modern_button(
                    top_frame,
                    text="✅",
                    command=enable_game,
                    font=('Segoe UI', 12),
                    bg=self.colors['success'],
                    hover_bg=self.colors['success_hover'],
                    width=3,
                    height=1,
                    padx=8,
                    pady=8
                )
                action_button.pack(side=tk.RIGHT, padx=(10, 0))
            else:
                # Disable button (red garbage bin) - for enabled games
                def disable_game():
                    success, message = self.disable_game(game['app_id'], game['game_name'])
                    if success:
                        # Update the game status immediately
                        game['is_disabled'] = True
                        game['lua_file'] = game['lua_file'] + '.disabled'
                        # Update the card in place
                        self.update_game_card_in_place(game, outer_frame)
                    else:
                        messagebox.showerror("Error", message)
                
                action_button = self.create_modern_button(
                    top_frame,
                    text="❌",
                    command=disable_game,
                    font=('Segoe UI', 12),
                    bg=self.colors['warning'],
                    hover_bg=self.colors['warning_hover'],
                    width=3,
                    height=1,
                    padx=8,
                    pady=8
                )
                action_button.pack(side=tk.RIGHT, padx=(10, 0))
            
            # Nuke button (🗑) - always show for installed games (pack last to be on far right)
            def nuke_game():
                # Show confirmation dialog
                result = messagebox.askyesno(
                    "Confirm Deletion",
                    f"Are you sure you want to DELETE the lua file for '{game['game_name']}'?\n\n"
                    "This will permanently remove the file and cannot be undone!",
                    icon='warning'
                )
                if result:
                    success, message = self.delete_lua_file(game['app_id'], game['game_name'])
                    if success:
                        # Update the game status immediately
                        game['is_installed'] = False
                        game['lua_file'] = None
                        # Update the card in place
                        self.update_game_card_in_place(game, outer_frame)
                    else:
                        messagebox.showerror("Error", message)
            
            nuke_button = self.create_modern_button(
                top_frame,
                text="🗑",
                command=nuke_game if not is_disabled else lambda: None,  # Disable command if game is disabled
                font=('Segoe UI', 12),
                bg=self.colors['error'] if not is_disabled else '#666666',  # Gray out if disabled
                hover_bg=self.colors['error_hover'] if not is_disabled else '#666666',  # No hover effect if disabled
                width=3,
                height=1,
                padx=8,
                pady=8
            )
            nuke_button.pack(side=tk.RIGHT, padx=(10, 0))
            
            # Disable the button if game is disabled
            if is_disabled:
                nuke_button.config(state='disabled')
        else:
            # Download button for non-installed games
            def download_game():
                # Add to download queue instead of downloading immediately
                self.add_to_download_queue(game['app_id'], game['game_name'])
                
                # Update button to show it's been queued
                action_button.config(state='disabled', text='Queued', bg='#FFA500')  # Orange
            
            # Check if game is queued
            app_id_str = str(game['app_id'])
            is_queued = app_id_str in self.queued_games
            
            action_button = self.create_modern_button(
                top_frame,
                text="Queued" if is_queued else "Download",
                command=download_game,
                font=('Segoe UI', 11, 'bold'),
                bg='#FFA500' if is_queued else self.colors['accent'],  # Orange if queued, modern indigo if not
                hover_bg='#FFA500' if is_queued else self.colors['accent_hover'],
                width=12,
                height=1,
                padx=15,
                pady=8
            )
            action_button.pack(side=tk.RIGHT, padx=(10, 0))
            
            # Disable button if queued
            if is_queued:
                action_button.config(state='disabled')
        
        # Game name (larger, bold) with installation status indicator
        if is_disabled:
            status_icon = "🔴"  # Red circle for disabled
            game_name_text = f"{status_icon} {game['game_name']} (DISABLED)"
            text_color = '#ff6b6b'  # Red text for disabled
        elif is_installed:
            status_icon = "🟢"  # Green circle for enabled
            game_name_text = f"{status_icon} {game['game_name']}"
            text_color = self.colors['text']
        else:
            status_icon = "❌"  # Red X for not installed
            game_name_text = f"{status_icon} {game['game_name']}"
            text_color = self.colors['text_muted']  # Use muted text color for non-installed
        
        # Game name label with fixed dimensions to prevent warping
        game_name_label = tk.Label(
            top_frame,
            text=game_name_text,
            font=('Segoe UI', 14, 'bold'),
            fg=text_color,
            bg=bg_color,
            anchor='w',
            wraplength=0,  # No text wrapping
            justify=tk.LEFT,
            cursor='hand2'  # Show hand cursor on hover
        )
        game_name_label.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 10))
        
        # Add hover effect to show it's clickable
        def on_enter(event):
            game_name_label.configure(fg=self.colors['accent'])
        
        def on_leave(event):
            game_name_label.configure(fg=text_color)
        
        game_name_label.bind('<Enter>', on_enter)
        game_name_label.bind('<Leave>', on_leave)
        
        # Add click bindings to game name
        def open_steamdb(event):
            """Open SteamDB page for the game"""
            import webbrowser
            app_id = str(game['app_id'])
            url = f"https://steamdb.info/app/{app_id}/"
            webbrowser.open(url)
        
        def open_lua_file(event):
            """Open the .lua file in default text editor"""
            if is_installed and game.get('lua_file'):
                import webbrowser
                import os
                import subprocess
                
                # Get Steam installation path
                steam_path = self.get_steam_install_path()
                if steam_path:
                    lua_file_path = os.path.join(steam_path, 'config', 'stplug-in', game['lua_file'])
                    if os.path.exists(lua_file_path):
                        try:
                            # Try to open with default text editor
                            if os.name == 'nt':  # Windows
                                os.startfile(lua_file_path)
                            else:  # Linux/Mac
                                subprocess.run(['xdg-open', lua_file_path])
                        except Exception as e:
                            # Fallback to webbrowser for some systems
                            webbrowser.open(f"file://{lua_file_path}")
                    else:
                        messagebox.showerror("Error", f"Could not find {game['lua_file']}")
                else:
                    messagebox.showerror("Error", "Could not find Steam installation path")
        
        # Bind left-click to open SteamDB
        game_name_label.bind('<Button-1>', open_steamdb)
        
        # Bind right-click to open .lua file
        game_name_label.bind('<Button-3>', open_lua_file)
        
        # Game info frame
        info_frame = tk.Frame(game_frame, bg=bg_color)
        info_frame.pack(fill=tk.X, padx=15, pady=(0, 10))
        
        # App ID and file info
        details_frame = tk.Frame(info_frame, bg=bg_color)
        details_frame.pack(fill=tk.X)
        
        # App ID (selectable/copyable)
        app_id_frame = tk.Frame(details_frame, bg=bg_color)
        app_id_frame.pack(side=tk.LEFT)
        
        app_id_label = tk.Label(
            app_id_frame,
            text="App ID: ",
            font=('Segoe UI', 10),
            fg=self.colors['accent'] if is_installed else '#666666',
            bg=bg_color,
            anchor='w'
        )
        app_id_label.pack(side=tk.LEFT)
        
        # Create a selectable label for the app ID
        app_id_value_label = tk.Label(
            app_id_frame,
            text=str(game['app_id']),
            font=('Segoe UI', 10),
            fg=self.colors['accent'] if is_installed else '#666666',
            bg=bg_color,
            anchor='w',
            cursor='ibeam'  # Show text cursor on hover
        )
        app_id_value_label.pack(side=tk.LEFT)
        
        # Add double-click to select all text (copy to clipboard)
        def select_all(event):
            # Copy the app ID to clipboard
            self.root.clipboard_clear()
            self.root.clipboard_append(str(game['app_id']))
            # Visual feedback - briefly change background color
            original_bg = app_id_value_label.cget('bg')
            app_id_value_label.configure(bg='#4a4a4a')
            self.root.after(200, lambda: app_id_value_label.configure(bg=original_bg))
            return 'break'
        
        app_id_value_label.bind('<Double-Button-1>', select_all)
        
        # Show game file name if setting is enabled
        show_file_names = self.settings.get('show_file_names', False)
        if show_file_names and is_installed and game.get('lua_file'):
            # Add spacing
            tk.Label(
                app_id_frame,
                text="  |  ",
                font=('Segoe UI', 10),
                fg=self.colors['accent'] if is_installed else '#666666',
                bg=bg_color
            ).pack(side=tk.LEFT)
            
            # File name label
            file_name_label = tk.Label(
                app_id_frame,
                text=f"File: {game['lua_file']}",
                font=('Segoe UI', 10),
                fg=self.colors['accent'] if is_installed else '#666666',
                bg=bg_color,
                anchor='w',
                cursor='ibeam'
            )
            file_name_label.pack(side=tk.LEFT)
            
            # Add double-click to copy file name
            def copy_file_name(event):
                self.root.clipboard_clear()
                self.root.clipboard_append(game['lua_file'])
                # Visual feedback
                original_bg = file_name_label.cget('bg')
                file_name_label.configure(bg='#4a4a4a')
                self.root.after(200, lambda: file_name_label.configure(bg=original_bg))
                return 'break'
            
            file_name_label.bind('<Double-Button-1>', copy_file_name)
        
        return outer_frame

    def open_game_list_settings(self):
        """Open the game list settings menu"""
        # Create a new top-level window for game list settings
        settings_window = tk.Toplevel(self.root)
        settings_window.title("Game List Settings")
        settings_window.geometry("500x400")
        settings_window.configure(bg=self.colors['bg'])
        
        # Center the window
        self.center_popup(settings_window)
        
        # Make the window modal
        settings_window.transient(self.root)
        settings_window.grab_set()
        
        # Settings title
        settings_title = tk.Label(
            settings_window,
            text="Game List Settings",
            font=('Segoe UI', 18, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        settings_title.pack(pady=(20, 10))
        
        # Create scrollable settings container
        canvas_frame = tk.Frame(settings_window, bg=self.colors['bg'])
        canvas_frame.pack(fill=tk.BOTH, expand=True, padx=20, pady=(0, 20))
        
        # Create canvas
        canvas = tk.Canvas(canvas_frame, bg=self.colors['bg'], highlightthickness=0)
        scrollbar = ttk.Scrollbar(canvas_frame, orient="vertical", command=canvas.yview)
        
        # Create the scrollable frame
        settings_container = tk.Frame(canvas, bg=self.colors['bg'])
        
        # Configure the canvas
        canvas.configure(yscrollcommand=scrollbar.set)
        
        # Pack the canvas and scrollbar
        canvas.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        
        # Create a window in the canvas for the settings container
        canvas_window = canvas.create_window((0, 0), window=settings_container, anchor="nw")
        
        # Configure the canvas to expand with the window
        def on_canvas_configure(event):
            canvas.configure(scrollregion=canvas.bbox("all"))
            canvas.itemconfig(canvas_window, width=event.width)
        
        def on_frame_configure(event):
            canvas.configure(scrollregion=canvas.bbox("all"))
        
        canvas.bind('<Configure>', on_canvas_configure)
        settings_container.bind('<Configure>', on_frame_configure)
        
        # Initialize setting variables dictionary for this window
        self.setting_vars = {}
        
        # Game list specific settings
        self.create_checkbox_setting(
            settings_container,
            "Show only installed games",
            "show_only_installed",
            "Only show installed games and search within them"
        )
        
        # Sort dropdown setting
        sort_options = [
            "smart sorting",
            "alphabetical A-Z",
            "alphabetical Z-A", 
            "last updated (installed only)",
            "last installed (installed only)"
        ]
        sort_dropdown = self.create_dropdown_setting(
            settings_container,
            "Sort by",
            "sort_by",
            sort_options,
            "Choose how to sort the game list"
        )
        
        # Add callback to automatically enable "show only installed" for time-based sorting
        def on_sort_change(event):
            if hasattr(self, 'setting_vars') and 'sort_by' in self.setting_vars and 'show_only_installed' in self.setting_vars:
                selected_sort = self.setting_vars['sort_by'].get()
                if selected_sort in ["last updated (installed only)", "last installed (installed only)"]:
                    # Auto-enable "show only installed" checkbox
                    self.setting_vars['show_only_installed'].set(True)
        
        sort_dropdown.bind('<<ComboboxSelected>>', on_sort_change)
        
        self.create_spinbox_setting(
            settings_container,
            "Search results limit",
            "search_results_limit",
            10, 500, 10,
            "Maximum number of search results to display (higher values may slow down search)"
        )
        
        self.create_spinbox_setting(
            settings_container,
            "Installed Games Shown Limit",
            "installed_games_shown_limit",
            5, 100, 5,
            "Maximum number of installed games to display when no search term is entered (default: 25)"
        )
        
        self.create_checkbox_setting(
            settings_container,
            "Show game file names",
            "show_file_names",
            "Display the actual .lua file names in the game cards"
        )
        
        # Don't start downloads until button pressed setting
        self.create_checkbox_setting(
            settings_container,
            "Start Download @ Button press",
            "dont_start_downloads_until_button_pressed",
            "Downloads only start when download manager button is clicked"
        )
        
        # Backup Downloads setting
        self.create_checkbox_setting(
            settings_container,
            "Backup Downloads",
            "backup_downloads",
            "Save files downloaded via LuaTools in \"melly-downloads\" folder"
        )
        
        # Button frame
        button_frame = tk.Frame(settings_window, bg=self.colors['bg'])
        button_frame.pack(fill=tk.X, padx=20, pady=(0, 20))
        
        # Save button
        def save_game_list_settings():
            # Save the settings
            for setting_key, var in self.setting_vars.items():
                if setting_key in ['show_only_installed', 'sort_by', 'search_results_limit', 'installed_games_shown_limit', 'show_file_names', 'dont_start_downloads_until_button_pressed', 'backup_downloads']:
                    self.settings[setting_key] = var.get()
            
            # Auto-enable "show only installed" for time-based sorting options
            if 'sort_by' in self.setting_vars:
                sort_by = self.setting_vars['sort_by'].get()
                if sort_by in ["last updated (installed only)", "last installed (installed only)"]:
                    self.settings['show_only_installed'] = True
                    # Update the checkbox in the UI if it exists
                    if 'show_only_installed' in self.setting_vars:
                        self.setting_vars['show_only_installed'].set(True)
            
            self.save_settings()
            
            # Apply settings immediately by refreshing the game display
            self.root.after(100, self.refresh_game_display_with_settings)
            
            settings_window.destroy()
            messagebox.showinfo("Settings Saved", "Game list settings have been saved and applied successfully!")
        
        save_button = tk.Button(
            button_frame,
            text="Save Settings",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=20,
            pady=5,
            cursor='hand2',
            command=save_game_list_settings
        )
        save_button.pack(side=tk.RIGHT, padx=(10, 0))
        
        # Cancel button
        cancel_button = tk.Button(
            button_frame,
            text="Cancel",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=20,
            pady=5,
            cursor='hand2',
            command=settings_window.destroy
        )
        cancel_button.pack(side=tk.RIGHT)
        
        # Load current settings
        for setting_key, var in self.setting_vars.items():
            if setting_key in ['show_only_installed', 'sort_by', 'search_results_limit', 'installed_games_shown_limit', 'show_file_names', 'dont_start_downloads_until_button_pressed', 'backup_downloads']:
                if setting_key in self.settings:
                    var.set(self.settings[setting_key])
        
        # Focus the window
        settings_window.focus_set()
        
        # Bind escape key to close
        settings_window.bind('<Escape>', lambda e: settings_window.destroy())
        
        # Bind mouse wheel to the entire settings window for scrolling anywhere
        def _on_mousewheel(event):
            # Scroll the canvas when mouse wheel is used anywhere in the settings window
            canvas.yview_scroll(int(-1*(event.delta/120)), "units")
        
        # Bind to the settings window to catch all mouse wheel events
        settings_window.bind("<MouseWheel>", _on_mousewheel)

    def refresh_game_display_with_settings(self):
        """Refresh the game display with current settings applied"""
        try:
            if hasattr(self, 'god_mode_frame') and self.god_mode_frame.winfo_exists():
                # Get current search term
                current_search = ""
                if hasattr(self, 'current_search_var'):
                    current_search = self.current_search_var.get()
                
                # Trigger the search function to refresh with new settings
                if hasattr(self, 'current_perform_search'):
                    self.current_perform_search()
                    
                    # Restore the search term if there was one
                    if current_search and hasattr(self, 'current_search_var'):
                        self.current_search_var.set(current_search)
                        
        except Exception as e:
            print(f"[REFRESH] Error refreshing game display with settings: {e}")

    def _create_buttons_impl(self, top_frame, game, is_installed, is_disabled):
        """Implementation of button creation to prevent frame warping"""
        try:
            # Safety check: ensure no buttons exist before creating new ones
            existing_buttons = []
            def check_existing_buttons(widget):
                if isinstance(widget, tk.Button):
                    existing_buttons.append(widget)
                for child in widget.winfo_children():
                    check_existing_buttons(child)
            
            check_existing_buttons(top_frame)
            
            if existing_buttons:
                print(f"[BUTTON_CREATION] Found {len(existing_buttons)} existing buttons, skipping creation")
                return
            
            print(f"[BUTTON_CREATION] Creating buttons for {game['game_name']} (installed: {is_installed}, disabled: {is_disabled})")
            if is_installed:
                # Nuke button (🗑) - always show for installed games
                def nuke_game():
                    # Show confirmation dialog
                    result = messagebox.askyesno(
                        "Confirm Deletion",
                        f"Are you sure you want to DELETE the lua file for '{game['game_name']}'?\n\n"
                        "This will permanently remove the file and cannot be undone!",
                        icon='warning'
                    )
                    if result:
                        success, message = self.delete_lua_file(game['app_id'], game['game_name'])
                        if success:
                            # Update the game status immediately
                            game['is_installed'] = False
                            game['lua_file'] = None
                            # Update the card in place
                            self.update_game_card_in_place(game, top_frame.master.master)
                        else:
                            messagebox.showerror("Error", message)
                
                if is_disabled:
                    # Enable button (green tick mark) - for disabled games
                    def enable_game():
                        success, message = self.enable_game(game['app_id'], game['game_name'])
                        if success:
                            # Update the game status immediately
                            game['is_disabled'] = False
                            game['lua_file'] = game['lua_file'].replace('.disabled', '')
                            # Update the card in place
                            self.update_game_card_in_place(game, top_frame.master.master)
                        else:
                            messagebox.showerror("Error", message)
                    
                    enable_button = self.create_modern_button(
                        top_frame,
                        text="✅",
                        command=enable_game,
                        font=('Segoe UI', 12),
                        bg=self.colors['success'],
                        hover_bg=self.colors['success_hover'],
                        width=3,
                        height=1,
                        padx=8,
                        pady=8
                    )
                    enable_button.pack(side=tk.RIGHT, padx=(10, 0))
                else:
                    # Disable button (red garbage bin) - for enabled games
                    def disable_game():
                        success, message = self.disable_game(game['app_id'], game['game_name'])
                        if success:
                            # Update the game status immediately
                            game['is_disabled'] = True
                            game['lua_file'] = game['lua_file'] + '.disabled'
                            # Update the card in place
                            self.update_game_card_in_place(game, top_frame.master.master)
                        else:
                            messagebox.showerror("Error", message)
                    
                    disable_button = self.create_modern_button(
                        top_frame,
                        text="❌",
                        command=disable_game,
                        font=('Segoe UI', 12),
                        bg=self.colors['warning'],
                        hover_bg=self.colors['warning_hover'],
                        width=3,
                        height=1,
                        padx=8,
                        pady=8
                    )
                    disable_button.pack(side=tk.RIGHT, padx=(10, 0))
                
                # Nuke button (🗑) - always show for installed games (pack last to be on far right)
                def nuke_game():
                    # Show confirmation dialog
                    result = messagebox.askyesno(
                        "Confirm Deletion",
                        f"Are you sure you want to DELETE the lua file for '{game['game_name']}'?\n\n"
                        "This will permanently remove the file and cannot be undone!",
                        icon='warning'
                    )
                    if result:
                        success, message = self.delete_lua_file(game['app_id'], game['game_name'])
                        if success:
                            # Update the game status immediately
                            game['is_installed'] = False
                            game['lua_file'] = None
                            # Update the card in place
                            self.update_game_card_in_place(game, top_frame.master.master)
                        else:
                            messagebox.showerror("Error", message)
                
                nuke_button = self.create_modern_button(
                    top_frame,
                    text="🗑",
                    command=nuke_game if not is_disabled else lambda: None,  # Disable command if game is disabled
                    font=('Segoe UI', 12),
                    bg=self.colors['error'] if not is_disabled else '#666666',  # Gray out if disabled
                    hover_bg=self.colors['error_hover'] if not is_disabled else '#666666',  # No hover effect if disabled
                    width=3,
                    height=1,
                    padx=8,
                    pady=8
                )
                nuke_button.pack(side=tk.RIGHT, padx=(10, 0))
                
                # Disable the button if game is disabled
                if is_disabled:
                    nuke_button.config(state='disabled')
            else:
                # Download button for non-installed games
                def download_game():
                    # Add to download queue instead of downloading immediately
                    self.add_to_download_queue(game['app_id'], game['game_name'])
                    
                    # Update button to show it's been queued
                    download_button.config(state='disabled', text='Queued', bg='#FFA500')  # Orange
                
                # Check if game is queued
                app_id_str = str(game['app_id'])
                is_queued = app_id_str in self.queued_games
                
                download_button = self.create_modern_button(
                    top_frame,
                    text="Queued" if is_queued else "Download",
                    command=download_game,
                    font=('Segoe UI', 11, 'bold'),
                    bg='#FFA500' if is_queued else self.colors['accent'],  # Orange if queued, modern indigo if not
                    hover_bg='#FFA500' if is_queued else self.colors['accent_hover'],
                    width=12,
                    height=1,
                    padx=15,
                    pady=8
                )
                download_button.pack(side=tk.RIGHT, padx=(10, 0))
                
                # Disable button if queued
                if is_queued:
                    download_button.config(state='disabled')
        except Exception as e:
            print(f"[BUTTON_CREATION] Error creating buttons: {e}")

    def import_settings_placeholder(self):
        """Placeholder function for importing settings"""
        messagebox.showinfo("Import Settings", "Import Settings functionality coming soon!")
    
    def import_games_placeholder(self):
        """Placeholder function for importing game list"""
        messagebox.showinfo("Import Game List", "Import Game List functionality coming soon!")
    
    def export_settings_placeholder(self):
        """Placeholder function for exporting settings"""
        messagebox.showinfo("Export Settings", "Export Settings functionality coming soon!")
    
    def export_games_placeholder(self):
        """Placeholder function for exporting game list"""
        messagebox.showinfo("Export Game List", "Export Game List functionality coming soon!")
    
    def import_section_clicked(self):
        """Placeholder function for import section click"""
        messagebox.showinfo("Import Section", "Import functionality coming soon!")
    
    def export_section_clicked(self):
        """Open the export menu to select games from Steam depot keys"""
        self.open_export_menu()
    
    def open_export_menu(self):
        """Open the export menu to select games from Steam depot keys"""
        # Hide current Import/Export content
        for widget in self.import_export_frame.winfo_children():
            widget.pack_forget()
        
        # Create export menu frame
        self.export_menu_frame = tk.Frame(self.import_export_frame, bg=self.colors['bg'])
        self.export_menu_frame.pack(fill=tk.BOTH, expand=True, padx=30, pady=30)
        
        # Title (removed to save space)
        
        # Search and controls frame
        search_frame = tk.Frame(self.export_menu_frame, bg=self.colors['bg'])
        search_frame.pack(fill=tk.X, pady=(20, 20))
        
        # Left side - Search
        search_left_frame = tk.Frame(search_frame, bg=self.colors['bg'])
        search_left_frame.pack(side=tk.LEFT, fill=tk.X, expand=True)
        
        search_label = tk.Label(
            search_left_frame,
            text="🔍 Search Games:",
            font=('Segoe UI', 12, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        search_label.pack(side=tk.LEFT, padx=(0, 10))
        
        self.export_search_var = tk.StringVar()
        self.export_search_var.trace('w', self.filter_export_games)
        search_entry = tk.Entry(
            search_left_frame,
            textvariable=self.export_search_var,
            font=('Segoe UI', 12),
            bg=self.colors['card_bg'],
            fg=self.colors['text'],
            relief=tk.FLAT,
            bd=0,
            highlightbackground=self.colors['border_light'],
            highlightthickness=1
        )
        search_entry.pack(side=tk.LEFT, fill=tk.X, expand=True, padx=(0, 20))
        
        # Right side - Controls
        controls_frame = tk.Frame(search_frame, bg=self.colors['bg'])
        controls_frame.pack(side=tk.RIGHT, fill=tk.Y)
        
        # Hide unknown games checkbox
        self.hide_unknown_games_var = tk.BooleanVar(value=True)  # Default to checked
        self.hide_unknown_games_var.trace('w', self.filter_export_games)
        hide_unknown_checkbox = tk.Checkbutton(
            controls_frame,
            text="Hide Unknown Games",
            variable=self.hide_unknown_games_var,
            bg=self.colors['bg'],
            activebackground=self.colors['bg'],
            selectcolor=self.colors['accent'],
            fg=self.colors['text'],
            activeforeground=self.colors['text'],
            font=('Segoe UI', 10)
        )
        hide_unknown_checkbox.pack(side=tk.TOP, pady=(0, 10))
        
        # Button frame for select/deselect
        button_frame = tk.Frame(controls_frame, bg=self.colors['bg'])
        button_frame.pack(side=tk.TOP)
        
        # Select all button
        select_all_button = self.create_modern_button(
            button_frame,
            text="☑ Select All",
            command=self.select_all_export_games,
            font=('Segoe UI', 10),
            bg=self.colors['button_secondary'],
            hover_bg=self.colors['button_secondary_hover'],
            padx=15,
            pady=6,
            width=12,
            height=1,
            disable_scaling=True
        )
        select_all_button.pack(side=tk.LEFT, padx=(0, 5))
        
        # Deselect all button
        deselect_all_button = self.create_modern_button(
            button_frame,
            text="☐ Deselect All",
            command=self.deselect_all_export_games,
            font=('Segoe UI', 10),
            bg=self.colors['button_secondary'],
            hover_bg=self.colors['button_secondary_hover'],
            padx=15,
            pady=6,
            width=12,
            height=1,
            disable_scaling=True
        )
        deselect_all_button.pack(side=tk.LEFT)
        
        # Games list frame with scrollbar
        list_frame = tk.Frame(self.export_menu_frame, bg=self.colors['bg'])
        list_frame.pack(fill=tk.BOTH, expand=True, pady=(0, 20))
        
        # Create canvas and scrollbar for games list
        self.export_canvas = tk.Canvas(
            list_frame,
            bg=self.colors['bg'],
            highlightthickness=0
        )
        self.export_scrollbar = ttk.Scrollbar(
            list_frame,
            orient="vertical",
            command=self.export_canvas.yview
        )
        self.export_scrollable_frame = tk.Frame(
            self.export_canvas,
            bg=self.colors['bg']
        )
        
        # Configure canvas to expand with window
        self.export_scrollable_frame.bind(
            "<Configure>",
            lambda e: self.export_canvas.configure(scrollregion=self.export_canvas.bbox("all"))
        )
        
        # Create window in canvas and configure it to expand
        self.export_canvas.create_window((0, 0), window=self.export_scrollable_frame, anchor="nw", width=self.export_canvas.winfo_width())
        self.export_canvas.configure(yscrollcommand=self.export_scrollbar.set)
        
        # Bind canvas resize to update scrollable frame width
        self.export_canvas.bind('<Configure>', self._on_export_canvas_configure)
        
        # Pack canvas and scrollbar
        self.export_canvas.pack(side="left", fill="both", expand=True)
        self.export_scrollbar.pack(side="right", fill="y")
        
        # Bind mouse wheel to canvas
        self.export_canvas.bind("<MouseWheel>", self._on_export_mousewheel)
        
        # Bottom buttons frame
        bottom_frame = tk.Frame(self.export_menu_frame, bg=self.colors['bg'])
        bottom_frame.pack(fill=tk.X, pady=(20, 0))
        
        # Back button
        back_button = self.create_modern_button(
            bottom_frame,
            text="← Back to Import/Export",
            command=self.back_to_import_export_from_export_menu,
            font=('Segoe UI', 10),
            bg=self.colors['button_secondary'],
            hover_bg=self.colors['button_secondary_hover'],
            padx=25,
            pady=10,
            width=20,
            height=1,
            disable_scaling=True
        )
        back_button.pack(side=tk.LEFT)
        
        # Export selected button
        export_selected_button = self.create_modern_button(
            bottom_frame,
            text="📤 Export Selected Games",
            command=self.export_selected_games,
            font=('Segoe UI', 10),
            bg=self.colors['accent'],
            hover_bg=self.colors['accent_hover'],
            padx=25,
            pady=10,
            width=20,
            height=1,
            disable_scaling=True
        )
        export_selected_button.pack(side=tk.RIGHT)
        
        # Load depot keys and populate games list
        self.load_depot_keys_and_populate_export_games()
        
        # Bind mouse wheel to the entire export menu frame for scrolling anywhere
        def _on_export_menu_mousewheel(event):
            # Scroll the export canvas when mouse wheel is used anywhere in the export menu
            self.export_canvas.yview_scroll(int(-1*(event.delta/120)), "units")
        
        # Bind to the export menu frame to catch all mouse wheel events
        self.export_menu_frame.bind("<MouseWheel>", _on_export_menu_mousewheel)
        
        # Also bind mouse wheel to all major child widgets for comprehensive coverage
        def bind_mousewheel_to_widget(widget):
            widget.bind("<MouseWheel>", _on_export_menu_mousewheel)
            # Recursively bind to all child widgets
            for child in widget.winfo_children():
                bind_mousewheel_to_widget(child)
        
        # Bind mouse wheel to all child widgets in the export menu
        bind_mousewheel_to_widget(self.export_menu_frame)
    
    def _on_export_mousewheel(self, event):
        """Handle mouse wheel scrolling in export games list"""
        self.export_canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")
    
    def _on_export_canvas_configure(self, event):
        """Handle canvas resize to ensure scrollable frame expands properly"""
        # Update the width of the scrollable frame to match canvas width
        canvas_width = event.width
        self.export_canvas.itemconfig(
            self.export_canvas.find_withtag("all")[0], 
            width=canvas_width
        )
    
    def load_depot_keys_and_populate_export_games(self):
        """Load depot keys from Steam config.vdf and populate export games list"""
        try:
            # Get Steam installation path
            steam_path = self.get_steam_install_path()
            if not steam_path:
                messagebox.showerror("Error", "Could not find Steam installation path")
                return
            
            # Look for config.vdf in Steam/config directory
            config_path = os.path.join(steam_path, "config", "config.vdf")
            if not os.path.exists(config_path):
                messagebox.showerror("Error", f"Could not find Steam config file at:\n{config_path}")
                return
            
            # Read and parse config.vdf
            depot_keys = self.parse_steam_config_vdf(config_path)
            if not depot_keys:
                messagebox.showerror("Error", "No depot decryption keys found in Steam config")
                return
            
            # Store depot keys for later use
            self.export_depot_keys = depot_keys
            
            # Populate games list
            self.populate_export_games_list(depot_keys)
            
        except Exception as e:
            messagebox.showerror("Error", f"Failed to load depot keys:\n{str(e)}")
            print(f"[EXPORT] Error loading depot keys: {e}")
    
    def parse_steam_config_vdf(self, config_path):
        """Parse Steam config.vdf file to extract depot decryption keys"""
        depot_keys = {}
        
        try:
            with open(config_path, 'r', encoding='utf-8', errors='ignore') as f:
                content = f.read()
            
            # Find the "depots" section
            depots_start = content.find('"depots"')
            if depots_start == -1:
                print("[EXPORT] No depots section found in config.vdf")
                return depot_keys
            
            # Extract the depots section content
            depots_content = content[depots_start:]
            
            # Find all depot entries with their decryption keys
            import re
            depot_pattern = r'"(\d+)"\s*\{\s*"DecryptionKey"\s*"([a-fA-F0-9]+)"'
            matches = re.findall(depot_pattern, depots_content)
            
            for app_id, decryption_key in matches:
                depot_keys[app_id] = decryption_key
                print(f"[EXPORT] Found depot key for App ID {app_id}: {decryption_key[:16]}...")
            
            print(f"[EXPORT] Total depot keys found: {len(depot_keys)}")
            return depot_keys
            
        except Exception as e:
            print(f"[EXPORT] Error parsing config.vdf: {e}")
            return depot_keys
    
    def populate_export_games_list(self, depot_keys):
        """Populate the export games list with games from depot keys"""
        # Clear existing list
        for widget in self.export_scrollable_frame.winfo_children():
            widget.destroy()
        
        # Store all games for filtering
        self.all_export_games = []
        
        # Create a fast lookup dictionary for Steam app names
        steam_app_lookup = {}
        if hasattr(self, '_steam_api_cache') and self._steam_api_cache:
            for app in self._steam_api_cache.get('applist', {}).get('apps', []):
                app_id = str(app.get('appid'))
                game_name = app.get('name', 'Unknown Game')
                steam_app_lookup[app_id] = game_name
            print(f"[EXPORT] Created lookup for {len(steam_app_lookup)} Steam apps")
        
        # Create games list
        for app_id, decryption_key in depot_keys.items():
            # Get game name from Steam lookup dictionary
            game_name = steam_app_lookup.get(app_id, f"Unknown Game (App ID: {app_id})")
            
            # Create game item frame
            game_frame = tk.Frame(self.export_scrollable_frame, bg=self.colors['card_bg'])
            game_frame.pack(fill=tk.X, pady=2, padx=5)
            
            # Checkbox for selection
            var = tk.BooleanVar()
            checkbox = tk.Checkbutton(
                game_frame,
                variable=var,
                bg=self.colors['card_bg'],
                activebackground=self.colors['card_bg'],
                selectcolor=self.colors['accent'],
                fg=self.colors['text'],
                activeforeground=self.colors['text']
            )
            checkbox.pack(side=tk.LEFT, padx=(10, 15))
            
            # Game info
            info_frame = tk.Frame(game_frame, bg=self.colors['card_bg'])
            info_frame.pack(side=tk.LEFT, fill=tk.X, expand=True)
            
            # Game name
            name_label = tk.Label(
                info_frame,
                text=game_name,
                font=('Segoe UI', 11, 'bold'),
                fg=self.colors['text'],
                bg=self.colors['card_bg'],
                anchor='w'
            )
            name_label.pack(anchor='w')
            
            # App ID
            app_id_label = tk.Label(
                info_frame,
                text=f"App ID: {app_id}",
                font=('Segoe UI', 9),
                fg=self.colors['text_secondary'],
                bg=self.colors['card_bg'],
                anchor='w'
            )
            app_id_label.pack(anchor='w')
            
            # Store game data
            game_data = {
                'app_id': app_id,
                'game_name': game_name,
                'decryption_key': decryption_key,
                'var': var,
                'frame': game_frame
            }
            self.all_export_games.append(game_data)
        
        print(f"[EXPORT] Populated {len(self.all_export_games)} games from depot keys")
        
        # Update scroll region
        self.export_canvas.update_idletasks()
        self.export_canvas.configure(scrollregion=self.export_canvas.bbox("all"))
        
        # Apply initial filtering since "Hide Unknown Games" is checked by default
        if hasattr(self, 'hide_unknown_games_var') and self.hide_unknown_games_var.get():
            self.filter_export_games()
    
    def get_game_name_from_cache(self, app_id):
        """Get game name from cached Steam API data"""
        if hasattr(self, '_steam_api_cache') and self._steam_api_cache:
            # Steam API returns data in format: {"applist": {"apps": [{"appid": 123, "name": "Game"}]}}
            apps = self._steam_api_cache.get('applist', {}).get('apps', [])
            
            # Convert app_id to int for comparison (Steam API uses integers)
            try:
                app_id_int = int(app_id)
                for app in apps:
                    if app.get('appid') == app_id_int:
                        return app.get('name', f"Unknown Game (App ID: {app_id})")
            except ValueError:
                # If app_id can't be converted to int, try string comparison
                for app in apps:
                    if str(app.get('appid')) == app_id:
                        return app.get('name', f"Unknown Game (App ID: {app_id})")
            
            print(f"[EXPORT] App ID {app_id} not found in Steam cache")
        return None
    
    def filter_export_games(self, *args):
        """Filter export games based on search text and hide unknown games setting"""
        visible_games = self._get_visible_export_games()
        
        # Hide all games first
        for game_data in self.all_export_games:
            game_data['frame'].pack_forget()
        
        # Show only visible games
        for game_data in visible_games:
            game_data['frame'].pack(fill=tk.X, pady=2, padx=5)
        
        # Update scroll region
        self.export_canvas.update_idletasks()
        self.export_canvas.configure(scrollregion=self.export_canvas.bbox("all"))
    
    def select_all_export_games(self):
        """Select all visible export games"""
        visible_games = self._get_visible_export_games()
        for game_data in visible_games:
            game_data['var'].set(True)
    
    def deselect_all_export_games(self):
        """Deselect all export games"""
        for game_data in self.all_export_games:
            game_data['var'].set(False)
    
    def _get_visible_export_games(self):
        """Get list of currently visible export games based on filters"""
        visible_games = []
        search_text = self.export_search_var.get().lower()
        hide_unknown = self.hide_unknown_games_var.get()
        
        for game_data in self.all_export_games:
            game_name = game_data['game_name'].lower()
            app_id = game_data['app_id'].lower()
            
            # Check if game should be hidden due to unknown status
            if hide_unknown and "unknown game" in game_name:
                continue
            
            # Check if game matches search text
            if not search_text or search_text in game_name or search_text in app_id:
                visible_games.append(game_data)
        
        return visible_games
    
    def export_selected_games(self):
        """Export selected games with their decryption keys to a file"""
        selected_games = []
        
        for game_data in self.all_export_games:
            if game_data['var'].get():
                selected_games.append({
                    'app_id': game_data['app_id'],
                    'game_name': game_data['game_name'],
                    'decryption_key': game_data['decryption_key']
                })
        
        if not selected_games:
            messagebox.showinfo("No Selection", "Please select at least one game to export.")
            return
        
        try:
            # Import tkinter file dialog
            from tkinter import filedialog
            
            # Open file save dialog
            filename = filedialog.asksaveasfilename(
                title="Save Export File",
                defaultextension=".luatools",
                filetypes=[("LuaTools Export", "*.luatools"), ("All Files", "*.*")],
                initialfile="export.luatools"
            )
            
            # Check if user cancelled the dialog
            if not filename:
                print("[EXPORT] User cancelled file save dialog")
                return
            
            # Write selected games to file
            with open(filename, 'w', encoding='utf-8') as f:
                for game in selected_games:
                    f.write(f"{game['app_id']}:{game['decryption_key']}\n")
            
            # Show success message
            messagebox.showinfo(
                "Export Complete", 
                f"Exported {len(selected_games)} decryption keys to {os.path.basename(filename)}"
            )
            
            print(f"[EXPORT] Successfully exported {len(selected_games)} games to {filename}")
            
        except Exception as e:
            error_msg = f"Failed to export games:\n{str(e)}"
            messagebox.showerror("Export Error", error_msg)
            print(f"[EXPORT] Error during export: {e}")
    
    def back_to_import_export_from_export_menu(self):
        """Return to Import/Export menu from export menu"""
        # Clean up export menu frame
        if hasattr(self, 'export_menu_frame'):
            self.export_menu_frame.destroy()
            delattr(self, 'export_menu_frame')
        
        # Show Import/Export menu again
        self.show_import_export_menu()
    
    def back_to_main_from_import_export(self):
        """Return to main UI from Import/Export menu"""
        # Clean up Import/Export frame
        if hasattr(self, 'import_export_frame'):
            self.import_export_frame.destroy()
            delattr(self, 'import_export_frame')
            
        # Show main UI again
        self.setup_ui()

    def open_update_disabler(self):
        """Open the Update Disabler popup menu"""
        # Create popup window
        popup = tk.Toplevel(self.root)
        popup.title("Update Disabler")
        popup.geometry("500x600")
        popup.configure(bg=self.colors['bg'])
        
        # Center the popup window
        self.center_popup(popup)
        
        # Make popup modal
        popup.transient(self.root)
        popup.grab_set()
        
        # Title
        title_label = tk.Label(
            popup,
            text="Update Disabler",
            font=('Segoe UI', 18, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        title_label.pack(pady=(20, 10))
        
        # AppID input frame (full width)
        input_frame = tk.Frame(popup, bg=self.colors['bg'])
        input_frame.pack(fill=tk.X, padx=20, pady=(0, 20))
        
        # Center the input elements within the full-width frame
        center_frame = tk.Frame(input_frame, bg=self.colors['bg'])
        center_frame.pack(expand=True)
        
        # AppID label
        appid_label = tk.Label(
            center_frame,
            text="AppID:",
            font=('Segoe UI', 12),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        appid_label.pack(side=tk.LEFT, padx=(0, 10))
        
        # AppID entry
        appid_var = tk.StringVar()
        appid_entry = tk.Entry(
            center_frame,
            textvariable=appid_var,
            font=('Segoe UI', 12),
            bg=self.colors['secondary_bg'],
            fg=self.colors['text'],
            insertbackground=self.colors['text'],
            relief=tk.FLAT,
            width=15
        )
        appid_entry.pack(side=tk.LEFT, padx=(0, 10))
        
        # Disable Updates button
        disable_button = tk.Button(
            center_frame,
            text="Disable Updates",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=15,
            pady=5,
            cursor='hand2',
            command=lambda: self.disable_updates_for_app(appid_var.get(), popup)
        )
        disable_button.pack(side=tk.LEFT, padx=(0, 10))
        
        # Close button (next to Disable Updates)
        close_button = tk.Button(
            center_frame,
            text="Close",
            font=('Segoe UI', 10),
            bg=self.colors['button_bg'],
            fg=self.colors['text'],
            activebackground=self.colors['button_hover'],
            activeforeground=self.colors['text'],
            relief=tk.FLAT,
            padx=15,
            pady=5,
            cursor='hand2',
            command=popup.destroy
        )
        close_button.pack(side=tk.LEFT)
        
        # Separator
        separator = tk.Frame(popup, height=2, bg=self.colors['secondary_bg'])
        separator.pack(fill=tk.X, padx=20, pady=(0, 20))
        
        # Disabled apps list title (centered)
        list_title = tk.Label(
            popup,
            text="Currently Disabled Apps:",
            font=('Segoe UI', 14, 'bold'),
            fg=self.colors['text'],
            bg=self.colors['bg']
        )
        list_title.pack(pady=(0, 10))
        
        # Create scrollable frame for disabled apps (full width)
        canvas = tk.Canvas(popup, bg=self.colors['bg'], highlightthickness=0)
        scrollbar = ttk.Scrollbar(popup, orient="vertical", command=canvas.yview)
        scrollable_frame = tk.Frame(canvas, bg=self.colors['bg'])
        
        scrollable_frame.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
        )
        
        # Create window that will expand to fill canvas width
        window_id = canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        
        # Configure canvas to expand properly
        def configure_canvas(event):
            canvas.itemconfig(window_id, width=event.width)
        canvas.bind('<Configure>', configure_canvas)
        
        # Make canvas expand to fill available space
        canvas.pack(side="left", fill="both", expand=True, padx=(20, 0))
        scrollbar.pack(side="right", fill="y", padx=(0, 20))
        
        # Store references for later use
        popup.scrollable_frame = scrollable_frame
        popup.canvas = canvas
        
        # Bind mouse wheel scrolling to the popup window
        popup.bind("<MouseWheel>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        popup.bind("<Button-4>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        popup.bind("<Button-5>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        
        # Bind mouse wheel scrolling to the canvas
        canvas.bind("<MouseWheel>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        canvas.bind("<Button-4>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        canvas.bind("<Button-5>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        
        # Bind mouse wheel scrolling to the scrollable frame
        scrollable_frame.bind("<MouseWheel>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        scrollable_frame.bind("<Button-4>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        scrollable_frame.bind("<Button-5>", lambda e: self._on_update_disabler_mousewheel(e, popup))
        
        # Populate the disabled apps list
        self.populate_disabled_apps_list(popup)

    def populate_disabled_apps_list(self, popup):
        """Populate the list of currently disabled apps"""
        # Get Steam installation path
        steam_path = self.get_steam_install_path()
        if not steam_path:
            return
        
        stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
        if not os.path.exists(stplugin_path):
            return
        
        # Find all .lua files
        lua_files, disabled_files = self.find_lua_files(stplugin_path)
        
        # Check which files have the updates disabled marker
        disabled_apps = []
        
        for lua_file in lua_files:
            try:
                with open(lua_file, 'r', encoding='utf-8') as f:
                    content = f.read()
                
                # Check if file contains LUATOOLS: UPDATES DISABLED! line
                if '-- LUATOOLS: UPDATES DISABLED!' in content:
                    app_id = self.extract_app_id(lua_file)
                    if app_id:
                        # Get game name from Steam API cache if available
                        game_name = "Unknown Game"
                        if hasattr(self, '_steam_api_cache'):
                            for app in self._steam_api_cache.get('applist', {}).get('apps', []):
                                if str(app.get('appid')) == str(app_id):
                                    game_name = app.get('name', 'Unknown Game')
                                    break
                        
                        disabled_apps.append({
                            'app_id': app_id,
                            'game_name': game_name,
                            'file_path': lua_file
                        })
            except Exception as e:
                print(f"Error reading {lua_file}: {e}")
                continue
        
        # Sort by game name
        disabled_apps.sort(key=lambda x: x['game_name'].lower())
        
        # Clear existing content
        for widget in popup.scrollable_frame.winfo_children():
            widget.destroy()
        
        if not disabled_apps:
            # Show "no disabled apps" message
            no_apps_label = tk.Label(
                popup.scrollable_frame,
                text="No apps currently have updates disabled",
                font=('Segoe UI', 12),
                fg=self.colors['text'],
                bg=self.colors['bg']
            )
            no_apps_label.pack(pady=20, padx=20)
            return
        
        # Create app cards
        for app in disabled_apps:
            app_frame = tk.Frame(popup.scrollable_frame, bg=self.colors['secondary_bg'], relief=tk.FLAT, bd=1)
            app_frame.pack(fill=tk.X, padx=10, pady=5)
            
            # App info (left side) - with proper spacing for X button
            info_frame = tk.Frame(app_frame, bg=self.colors['secondary_bg'])
            info_frame.pack(side=tk.LEFT, fill=tk.BOTH, expand=True, padx=(10, 50), pady=10)
            
            # Game name
            name_label = tk.Label(
                info_frame,
                text=app['game_name'],
                font=('Segoe UI', 12, 'bold'),
                fg=self.colors['text'],
                bg=self.colors['secondary_bg']
            )
            name_label.pack(anchor=tk.W)
            
            # App ID
            appid_label = tk.Label(
                info_frame,
                text=f"App ID: {app['app_id']}",
                font=('Segoe UI', 10),
                fg=self.colors['text'],
                bg=self.colors['secondary_bg']
            )
            appid_label.pack(anchor=tk.W)
            
            # Enable button (right side) - positioned to not overlap text
            enable_button = tk.Button(
                app_frame,
                text="✕",  # X symbol
                font=('Segoe UI', 12, 'bold'),
                bg='#ff6b6b',  # Red color
                fg='white',
                activebackground='#ff5252',
                activeforeground='white',
                relief=tk.FLAT,
                width=3,
                height=1,
                cursor='hand2',
                command=lambda a=app: self.enable_updates_for_app(a, popup)
            )
            enable_button.pack(side=tk.RIGHT, padx=10, pady=10)
        
        # Update canvas scroll region
        popup.canvas.update_idletasks()
        popup.canvas.configure(scrollregion=popup.canvas.bbox("all"))

    def disable_updates_for_app(self, app_id, popup):
        """Disable updates for a specific app by adding the marker and uncommenting setManifestid lines"""
        if not app_id or not app_id.strip():
            messagebox.showwarning("Invalid AppID", "Please enter a valid AppID")
            return
        
        app_id = app_id.strip()
        
        # Get Steam installation path
        steam_path = self.get_steam_install_path()
        if not steam_path:
            messagebox.showerror("Error", "Could not find Steam installation path")
            return
        
        stplugin_path = os.path.join(steam_path, 'config', 'stplug-in')
        if not os.path.exists(stplugin_path):
            messagebox.showerror("Error", "Could not find stplug-in directory")
            return
        
        # Look for the .lua file
        lua_file_path = os.path.join(stplugin_path, f"{app_id}.lua")
        if not os.path.exists(lua_file_path):
            messagebox.showerror("Error", f"Could not find {app_id}.lua file in stplug-in directory")
            return
        
        try:
            # Read the file
            with open(lua_file_path, 'r', encoding='utf-8') as f:
                content = f.read()
            
            lines = content.split('\n')
            
            # Check if updates are already disabled
            if '-- LUATOOLS: UPDATES DISABLED!' in content:
                messagebox.showinfo("Already Disabled", f"Updates for {app_id} are already disabled")
                return
            
            # Add the updates disabled marker at the beginning
            lines.insert(0, '-- LUATOOLS: UPDATES DISABLED!')
            
            # Uncomment all setManifestid lines (remove -- prefix)
            modified = False
            for i, line in enumerate(lines):
                if line.strip().startswith('--setManifestid'):
                    lines[i] = line[2:]  # Remove the -- prefix
                    modified = True
            
            # Write the modified file
            with open(lua_file_path, 'w', encoding='utf-8') as f:
                f.write('\n'.join(lines))
            
            # Get game name for display
            game_name = "Unknown Game"
            if hasattr(self, '_steam_api_cache'):
                for app in self._steam_api_cache.get('applist', {}).get('apps', []):
                    if str(app.get('appid')) == str(app_id):
                        game_name = app.get('name', 'Unknown Game')
                        break
            
            # Show success message
            messagebox.showinfo(
                "Updates Disabled", 
                f"Updates have been disabled for {game_name} (App ID: {app_id})\n\n"
                f"The file has been modified and setManifestid lines have been uncommented."
            )
            
            # Refresh the disabled apps list
            self.populate_disabled_apps_list(popup)
            
            # Clear the AppID input
            for widget in popup.winfo_children():
                if isinstance(widget, tk.Frame) and widget.winfo_children():
                    for child in widget.winfo_children():
                        if isinstance(child, tk.Entry):
                            child.delete(0, tk.END)
                            break
            
            # Refresh the main game list if God Mode is open
            if hasattr(self, 'god_mode_frame') and hasattr(self, 'god_mode_game_list'):
                self.refresh_game_display_only()
            
        except Exception as e:
            messagebox.showerror("Error", f"Failed to disable updates for {app_id}: {str(e)}")

    def enable_updates_for_app(self, app, popup):
        """Enable updates for a specific app by removing the marker and commenting setManifestid lines"""
        app_id = app['app_id']
        game_name = app['game_name']
        file_path = app['file_path']
        
        try:
            # Read the file
            with open(file_path, 'r', encoding='utf-8') as f:
                content = f.read()
            
            lines = content.split('\n')
            
            # Remove the updates disabled marker
            if '-- LUATOOLS: UPDATES DISABLED!' in lines:
                lines.remove('-- LUATOOLS: UPDATES DISABLED!')
            
            # Comment all setManifestid lines (add -- prefix)
            modified = False
            for i, line in enumerate(lines):
                if line.strip().startswith('setManifestid'):
                    lines[i] = '--' + line
                    modified = True
            
            # Write the modified file
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write('\n'.join(lines))
            
            # Show success message
            messagebox.showinfo(
                "Updates Enabled", 
                f"Updates have been enabled for {game_name} (App ID: {app_id})\n\n"
                f"The file has been modified and setManifestid lines have been commented out."
            )
            
            # Refresh the disabled apps list
            self.populate_disabled_apps_list(popup)
            
            # Refresh the main game list if God Mode is open
            if hasattr(self, 'god_mode_frame') and hasattr(self, 'god_mode_game_list'):
                self.refresh_game_display_only()
            
        except Exception as e:
            messagebox.showerror("Error", f"Failed to enable updates for {app_id}: {str(e)}")

    def _on_update_disabler_mousewheel(self, event, popup):
        """Handle mouse wheel scrolling in the Update Disabler popup"""
        try:
            if event.delta:
                # Windows
                popup.canvas.yview_scroll(int(-1 * (event.delta / 120)), "units")
            elif event.num == 4:
                # Linux scroll up
                popup.canvas.yview_scroll(-1, "units")
            elif event.num == 5:
                # Linux scroll down
                popup.canvas.yview_scroll(1, "units")
        except:
            pass

    def show_import_export_dev_message(self):
        """Show development message for Import/Export feature"""
        messagebox.showinfo(
            "Currently In Development",
            "Currently In Development, if you know anything about how decryption keys are extracted pls dm me on discord @malonin0807"
        )

def main():
    root = tk.Tk()
    app = SteamStyleApp(root)
    root.mainloop()

if __name__ == "__main__":
    main() 
