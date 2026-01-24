# Development instructions and useful information

Check the following sections for rules and useful information.

## Sub Agents

Pick and use sub agents if their proficiency matches with the tasks at hand and their use adds value and efficiency to the task.

## Development environment

* The project is developed in Windows 10 with .NET 10 ASP.NET MVC. The database is PostgreSQL with PostGIS. Front end is plain modern JavaScript with preference in arrow functions. For tile services uses Open Street Map but with local cache in order to respect fair use policies and Leaflet library.

## Application Description

* The app is a Google Timeline and Google MyMaps alternative, enabling users to track their historical location data and design trips on maps. Users can import their Google exported timeline json into the app, export their timeline from the app, import Google MyMaps designed maps and trips into the app, export maps and trips from the app and into Google MyMaps. Also the app cares about privacy so user's location timeline and designed trips have options to switch to public/private before being available to public.

* The app also has a mobile version that uses API token access to web app backend (this application) that loads user's trips and can also navigate to places both saved and temporary. Also can provide user his current location on map. For map uses dynamic tile services with default Open Street Map.

## App - Code Structure

* The application uses the ASP Areas system and has the following areas:

  * Admin (for application wide administrator features)
  * Api where all the exernally related API endpoints are
  * Identity for indentity related actions (ASP.NET Identity)
  * Manager for buisiness related administrative actions
  * User for all user related actions (API token management, timeline and saved location handling, trip handling [create,edit,delete,publish, etc]), import/export location and trip data.
  * Public for public facing actions (Public users timeline sharing, public user trips sharing)

* in wwwroot:

  * js/lib directory contains 3rd party js libraries
  * js/Areas follows loosely the Areas structure having js modules or files for each Area or/and it's Views

## Code style

* All code new and old must be documented so everytime you edit code and does not contain documentation you will have to add it.
* NEVER create files unless they're absolutely necessary for achieving your goal. The same stands with services, classes, methods and variables.
* ALWAYS prefer editing an existing file to creating a new one. The same stands with services, classes, methods and variables.

## Project files

* Backend this application - web app is found at C:\Users\stef\source\repos\Wayfarer
* Mobile app is found at C:\Users\stef\source\repos\WayfarerMobile
