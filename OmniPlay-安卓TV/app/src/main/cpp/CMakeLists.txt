cmake_minimum_required(VERSION 3.22.1)
project(omniplay_mpvbridge)

add_library(mpvbridge SHARED mpv_bridge.cpp)

find_library(log-lib log)
find_library(android-lib android)
find_library(dl-lib dl)

target_link_libraries(mpvbridge ${log-lib} ${android-lib} ${dl-lib})
