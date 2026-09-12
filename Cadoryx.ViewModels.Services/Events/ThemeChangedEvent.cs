using System;
using System.Collections.Generic;
using System.Text;

namespace Cadoryx.ViewModels.Services.Events;

public record class ThemeChangedEvent(bool IsDark);
