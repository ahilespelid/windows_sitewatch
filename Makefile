# SiteWatch: make = build.cmd (встроенный в Windows csc.exe, .NET Framework 4.x)

all:
	cmd //c build.cmd

clean:
	rm -f SiteWatch.exe

.PHONY: all clean
