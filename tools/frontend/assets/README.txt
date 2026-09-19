Graphics for the configurator front end.

Every file is OPTIONAL. If it is missing, the window shows a placeholder in the
palette at the same spot and the layout does not change. That way the layout can
be finished before the assets are, and each image can be added on its own.

  logo.png         Logo lockup, transparent, around 600 x 156 px
                   CUT IT OUT of the moodboard rather than generating it - an
                   image model will invent a similar wordmark, and then the front
                   end and the moodboard no longer match.

  avatar.png       The VR avatar, transparent cutout, 256 x 256 px
                   Currently sits on white, so it only needs the background
                   removed. The window clips it to a circle.

  background.png   Background plate, 2560 x 1600 px
                   Heavily blurred and with no focal point. It stands in for the
                   real-time blur that WPF does not do well.

Later, once the layout is settled:

  texture-drops.png   Droplet overlay, transparent, 2048 x 2048
  icon-*.png          Symbols, transparent, 256 x 256 each

Palette from the moodboard, held in the XAML as named resources:

  #12191B  Ink     base surface
  #083B43  Deep    panels and gradient
  #15D9D0  Cyan    accent, active states
  #F5FCFA  Paper   text
  #FFD447  Amber   the ONE primary action
  #FF6B55  Coral   warnings and errors
