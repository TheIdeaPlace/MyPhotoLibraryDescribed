# Introducing My Photo Library Described

*Draft (Kelly, 13 Oct) for the README and a possible blog post. Not published.*

My Photo Library Described (MPLD) is aimed at getting photos from your photo library described as easily as they are shown on screen. Launch MPLD and the photo library for the computer you're running it on is shown in a list: your Photos library on the Mac, and your Pictures library on Windows. Arrow to each photo and the AI technology built into your computer's operating system takes over and describes it.

MPLD is intended to make describing photos automatic. It's also an example of features that could be added to the photo apps that come with the Mac and Windows.

By default, descriptions are read automatically as you arrow to each photo, and can be repeated with Ctrl+D (Windows) or Command-D (Mac). You can have a photo described afresh with Ctrl+R (Windows) or Command-R (Mac). Pressing Ctrl+Shift+D (Windows) or Shift-Command-D (Mac) moves focus between the list of photos and a text box with the description, where you can review it in detail. To read description after description without leaving that box (with a braille display, say), Alt+Right and Alt+Left (Windows) or Command-Right and Command-Left (Mac) step to the next or previous photo and leave you where you are.

The Help menu has Check System, which checks your computer can describe photos with MPLD. It needs specific hardware and operating system versions:

- **Mac:** a Mac with Apple silicon running macOS 27 or later, with Apple Intelligence turned on. (MPLD opens on macOS 15 or later, but describes photos only on macOS 27.)
- **Windows:** a Copilot+ PC, which has an NPU, running Windows 11 version 24H2 or later.

Help > Check AI Description describes a sample photo that comes with MPLD, to show that describing works on your computer. If your computer doesn't meet the requirements, the [Image Description Toolkit](https://github.com/TheIdeaPlace/Image-Description-Toolkit) can describe photos on a much wider range of hardware, with a choice of AI models.

MPLD can also describe the photos in your library in the background, so their descriptions are ready the moment you arrow to them rather than taking the couple of seconds a new description needs. Turn this on or off with Describe Photos in the Background in the Photo menu. It works through every photo not described yet, newest first, picks up where it left off each time MPLD starts, and the status bar shows how far it's got.

When MPLD launches, you'll notice it says it's in Read-Only Mode. This means that, aside from reading your photos to describe and show them, MPLD doesn't change your image files at all. Descriptions are kept in MPLD's own files, not in your photos.

If you turn on Editing Mode (turn off Read-Only Mode in the File menu, which asks first), MPLD can also:

- write or change a photo's caption (Edit Caption);
- make a photo's description its caption, and its alt text for screen readers (Use Description as Caption);
- save each new description into the photo automatically, filling in its caption and alt text when they're empty (Save Descriptions in Photos);
- mark photos as favorites, and give them a rating.

Nothing you or another app has written is replaced without asking. Each launch starts in Read-Only Mode again.

MPLD keeps your photos on your computer: they're described there, never uploaded. The only things it sends over the internet are checks for updates and, if you turn on Look Up Place Names, a photo's location so Get Info can name the place it was taken. The [privacy policy](PRIVACY.md) has the details, and the [user guide](user-guide.md) covers everything else.
